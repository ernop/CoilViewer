using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CoilViewer;

public sealed class ObjectDetectionService : IDisposable
{
    private InferenceSession? _session;
    private readonly object _lock = new();
    private bool _isInitialized;
    private int _inputSize = 224; // Will be detected from model or configured
    private string[]? _classLabels;
    private int _totalImagesChecked = 0;
    private long _totalMilliseconds = 0;
    
    public int InputSize => _inputSize;

    public bool IsAvailable => _isInitialized && _session != null;
    
    public int TotalImagesChecked => _totalImagesChecked;
    public double AverageMillisecondsPerImage => _totalImagesChecked > 0 ? (double)_totalMilliseconds / _totalImagesChecked : 0;

    public void Initialize(string modelPath, string? labelsPath = null, int configuredInputSize = 0)
    {
        if (_isInitialized)
        {
            return;
        }

        lock (_lock)
        {
            if (_isInitialized)
            {
                return;
            }

            try
            {
                if (!File.Exists(modelPath))
                {
                    Logger.Log($"Object detection model not found at '{modelPath}'. Object detection will be disabled.");
                    _isInitialized = true;
                    return;
                }

                var options = new SessionOptions();
                Logger.Log("Object detection initialized with CPU (install Microsoft.ML.OnnxRuntime.Gpu for GPU support).");

                _session = new InferenceSession(modelPath, options);

                // Detect input size from model metadata or use configured size
                if (configuredInputSize > 0)
                {
                    _inputSize = configuredInputSize;
                    Logger.Log($"Using configured input size: {_inputSize}x{_inputSize}");
                }
                else
                {
                    DetectInputSize();
                }

                // Load ImageNet class labels if provided
                if (!string.IsNullOrWhiteSpace(labelsPath) && File.Exists(labelsPath))
                {
                    _classLabels = File.ReadAllLines(labelsPath);
                    Logger.Log($"Loaded {_classLabels.Length} class labels from '{labelsPath}'");
                }
                else
                {
                    // Use default ImageNet labels (1000 classes)
                    _classLabels = GetDefaultImageNetLabels();
                    Logger.Log("Using default ImageNet class labels (1000 classes)");
                }

                _isInitialized = true;
                Logger.Log($"Object detection service initialized successfully with model: {modelPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize object detection service: {ex.Message}", ex);
                _session?.Dispose();
                _session = null;
                _isInitialized = true;
            }
        }
    }

    public ObjectDetectionResult? DetectObjects(string imagePath, int topK = 5)
    {
        if (!IsAvailable || _session == null)
        {
            Logger.Log($"Object detection skipped for '{imagePath}' (service unavailable).");
            return null;
        }

        try
        {
            using var bitmap = LoadImageAsBitmap(imagePath);
            var result = DetectObjects(bitmap, topK);

            if (result != null)
            {
                var summary = string.Join(", ", result.Predictions.Select(p => $"{p.ClassName}:{p.Confidence:F3}"));
                Logger.Log($"Object detection for '{imagePath}' (top {topK}): {summary}");
            }
            else
            {
                Logger.Log($"Object detection for '{imagePath}' returned no result.");
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to detect objects in '{imagePath}'", ex);
            return null;
        }
    }

    public ObjectDetectionResult? DetectObjects(Bitmap bitmap, int topK = 5)
    {
        if (!IsAvailable || _session == null)
        {
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Preprocess image with high-quality bicubic interpolation
            using var resized = ResizeImageHighQuality(bitmap, _inputSize, _inputSize);
            var input = PreprocessImage(resized, _inputSize);

            // Get input name from model metadata
            var inputName = _session.InputMetadata.Keys.First();
            var inputTensor = new DenseTensor<float>(input, new[] { 1, 3, _inputSize, _inputSize });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            // Run inference
            using var results = _session.Run(inputs);
            var output = results.First().Value as DenseTensor<float>;

            if (output == null)
            {
                stopwatch.Stop();
                return null;
            }

            // Get top K predictions
            var logits = output.ToArray();
            
            // Apply softmax to convert logits to probabilities
            var probabilities = Softmax(logits);
            
            var topPredictions = probabilities
                .Select((prob, index) => new { Index = index, Probability = prob })
                .OrderByDescending(x => x.Probability)
                .Take(topK)
                .Select(x => new ObjectPrediction
                {
                    ClassIndex = x.Index,
                    ClassName = GetClassName(x.Index),
                    Confidence = x.Probability
                })
                .ToList();

            stopwatch.Stop();
            lock (_lock)
            {
                _totalImagesChecked++;
                _totalMilliseconds += stopwatch.ElapsedMilliseconds;
                
                // Log performance stats every 10 images
                if (_totalImagesChecked % 10 == 0)
                {
                    var avgMs = AverageMillisecondsPerImage;
                    var totalSeconds = _totalMilliseconds / 1000.0;
                    Logger.Log($"Object detection: {_totalImagesChecked} images processed, {totalSeconds:F1}s total, {avgMs:F1}ms avg per image");
                }
            }

            return new ObjectDetectionResult
            {
                Predictions = topPredictions
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError("Failed to detect objects in image", ex);
            return null;
        }
    }

    public bool ContainsObject(string imagePath, string searchTerm, float minConfidence = 0.1f)
    {
        var result = DetectObjects(imagePath, topK: 10);
        if (result == null)
        {
            return false;
        }

        var searchLower = searchTerm.ToLowerInvariant();
        var contains = result.Predictions.Any(p => 
            p.Confidence >= minConfidence && 
            p.ClassName.ToLowerInvariant().Contains(searchLower));

        Logger.Log($"Object detection contains check for '{imagePath}' (term='{searchTerm}', min_conf={minConfidence:F2}): {contains}");
        return contains;
    }

    private void DetectInputSize()
    {
        if (_session == null)
        {
            return;
        }

        try
        {
            // Get the first input metadata
            var inputMeta = _session.InputMetadata.First();
            var inputName = inputMeta.Key;
            var inputInfo = inputMeta.Value;
            
            // Try to extract dimensions from shape
            if (inputInfo.Dimensions != null && inputInfo.Dimensions.Length >= 4)
            {
                // Typical shape is [batch, channels, height, width] or [batch, height, width, channels]
                // For NCHW format (most common): [1, 3, H, W]
                // For NHWC format: [1, H, W, 3]
                var dims = inputInfo.Dimensions;
                
                // Look for the spatial dimensions (should be equal for square inputs)
                int detectedSize = 224; // default fallback
                
                if (dims.Length == 4)
                {
                    // NCHW format: dims[2] and dims[3] are height and width
                    if (dims[2] > 0 && dims[2] == dims[3])
                    {
                        detectedSize = dims[2];
                    }
                    // NHWC format: dims[1] and dims[2] are height and width
                    else if (dims[1] > 0 && dims[1] == dims[2] && dims[3] == 3)
                    {
                        detectedSize = dims[1];
                    }
                }
                
                _inputSize = detectedSize;
                Logger.Log($"Model input shape: [{string.Join(", ", dims)}]");
                Logger.Log($"Detected input size: {_inputSize}x{_inputSize}");
            }
            else
            {
                Logger.Log($"Model input metadata: {inputInfo.Dimensions?.Length ?? 0} dimensions");
                Logger.Log($"Using default input size: {_inputSize}x{_inputSize}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to detect input size from model, using default {_inputSize}x{_inputSize}", ex);
        }
    }

    private string GetClassName(int index)
    {
        if (_classLabels != null && index >= 0 && index < _classLabels.Length)
        {
            return _classLabels[index];
        }
        return $"Class_{index}";
    }

    private static Bitmap LoadImageAsBitmap(string imagePath)
    {
        // Use WPF's BitmapDecoder to load the image - supports all formats including WebP
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        
        // Convert to a format suitable for System.Drawing.Bitmap
        var bitmap = new Bitmap(frame.PixelWidth, frame.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            bitmap.PixelFormat);
        
        try
        {
            frame.CopyPixels(System.Windows.Int32Rect.Empty, bitmapData.Scan0, 
                bitmapData.Height * bitmapData.Stride, bitmapData.Stride);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
        
        return bitmap;
    }

    private static Bitmap ResizeImageHighQuality(Bitmap source, int targetWidth, int targetHeight)
    {
        // Use high-quality bicubic interpolation for best results
        var resized = new Bitmap(targetWidth, targetHeight);
        using (var graphics = Graphics.FromImage(resized))
        {
            // Set high-quality rendering options for best accuracy
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            
            graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        }
        return resized;
    }

    private static float[] PreprocessImage(Bitmap bitmap, int inputSize)
    {
        // ImageNet normalization: (pixel / 255.0 - mean) / std
        const float meanR = 0.485f;
        const float meanG = 0.456f;
        const float meanB = 0.406f;
        const float stdR = 0.229f;
        const float stdG = 0.224f;
        const float stdB = 0.225f;

        var data = new float[3 * inputSize * inputSize];
        
        // Use LockBits for much faster pixel access
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        
        try
        {
            unsafe
            {
                byte* scan0 = (byte*)bitmapData.Scan0;
                int stride = bitmapData.Stride;
                
                int index = 0;
                for (int y = 0; y < inputSize; y++)
                {
                    byte* row = scan0 + (y * stride);
                    for (int x = 0; x < inputSize; x++)
                    {
                        // BGRA format in memory
                        byte b = row[x * 4];
                        byte g = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];
                        
                        // Store in CHW format (Channel, Height, Width) with normalization
                        data[index] = (r / 255.0f - meanR) / stdR; // R channel
                        data[index + inputSize * inputSize] = (g / 255.0f - meanG) / stdG; // G channel
                        data[index + (2 * inputSize * inputSize)] = (b / 255.0f - meanB) / stdB; // B channel
                        index++;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return data;
    }

    private static float[] Softmax(float[] logits)
    {
        // Find max value for numerical stability
        var max = logits.Max();
        
        // Compute exp(x - max) for each element
        var exps = logits.Select(x => Math.Exp(x - max)).ToArray();
        
        // Compute sum of exponentials
        var sumExps = exps.Sum();
        
        // Normalize to get probabilities
        return exps.Select(x => (float)(x / sumExps)).ToArray();
    }

    private static string[] GetDefaultImageNetLabels()
    {
        // Common ImageNet class names (abbreviated - full list has 1000 classes)
        // For production, load from a file. This is a fallback.
        return new string[]
        {
            "background", "tench", "goldfish", "great white shark", "tiger shark",
            "hammerhead", "electric ray", "stingray", "cock", "hen",
            "ostrich", "brambling", "goldfinch", "house finch", "junco",
            "indigo bunting", "robin", "bulbul", "jay", "magpie",
            "chickadee", "water ouzel", "kite", "bald eagle", "vulture",
            "great grey owl", "European fire salamander", "common newt", "eft",
            "spotted salamander", "axolotl", "bullfrog", "tree frog", "tailed frog",
            "loggerhead", "leatherback turtle", "mud turtle", "terrapin", "box turtle",
            "banded gecko", "common iguana", "American chameleon", "whiptail", "agama",
            "frilled lizard", "alligator lizard", "Gila monster", "green lizard", "African chameleon",
            "Komodo dragon", "African crocodile", "American alligator", "triceratops", "thunder snake",
            "ringneck snake", "hognose snake", "green snake", "king snake", "garter snake",
            "water snake", "vine snake", "night snake", "boa constrictor", "rock python",
            "Indian cobra", "green mamba", "sea snake", "horned viper", "diamondback",
            "sidewinder", "trilobite", "harvestman", "scorpion", "black and gold garden spider",
            "barn spider", "garden spider", "black widow", "tarantula", "wolf spider",
            "tick", "centipede", "black grouse", "ptarmigan", "ruffed grouse",
            "prairie chicken", "peacock", "quail", "partridge", "African grey",
            "macaw", "sulphur-crested cockatoo", "lorikeet", "coucal", "bee eater",
            "hornbill", "hummingbird", "jacamar", "toucan", "drake",
            "red-breasted merganser", "goose", "black swan", "tusker", "echidna",
            "platypus", "wallaby", "koala", "wombat", "jellyfish",
            "sea anemone", "brain coral", "flatworm", "nematode", "conch",
            "snail", "slug", "sea slug", "chiton", "chambered nautilus",
            "king crab", "American lobster", "spiny lobster", "crayfish", "hermit crab",
            "isopod", "white stork", "black stork", "spoonbill", "flamingo",
            "little blue heron", "American egret", "bittern", "crane", "limpkin",
            "European gallinule", "American coot", "bustard", "ruddy turnstone", "red-backed sandpiper",
            "redshank", "dowitcher", "oystercatcher", "pelican", "king penguin",
            "albatross", "grey whale", "killer whale", "dugong", "sea lion",
            "Chihuahua", "Japanese spaniel", "Maltese dog", "Pekinese", "Shih-Tzu",
            "Blenheim spaniel", "papillon", "toy terrier", "Rhodesian ridgeback", "Afghan hound",
            "basset", "beagle", "bloodhound", "bluetick", "black-and-tan coonhound",
            "Walker hound", "English foxhound", "redbone", "borzoi", "Irish wolfhound",
            "Italian greyhound", "whippet", "Ibizan hound", "Norwegian elkhound", "otterhound",
            "Saluki", "Scottish deerhound", "Weimaraner", "Staffordshire bullterrier", "American Staffordshire terrier",
            "Bedlington terrier", "Border terrier", "Kerry blue terrier", "Irish terrier", "Norfolk terrier",
            "Norwich terrier", "Yorkshire terrier", "wire-haired fox terrier", "Lakeland terrier", "Sealyham terrier",
            "Airedale", "cairn", "Australian terrier", "Dandie Dinmont", "Boston bull",
            "miniature schnauzer", "giant schnauzer", "standard schnauzer", "Scotch terrier", "Tibetan terrier",
            "silky terrier", "soft-coated wheaten terrier", "West Highland white terrier", "Lhasa", "flat-coated retriever",
            "curly-coated retriever", "golden retriever", "Labrador retriever", "Chesapeake Bay retriever", "German short-haired pointer",
            "vizsla", "English setter", "Irish setter", "Gordon setter", "Brittany spaniel",
            "clumber", "English springer", "Welsh springer spaniel", "cocker spaniel", "Sussex spaniel",
            "Irish water spaniel", "kuvasz", "schipperke", "groenendael", "malinois",
            "briard", "kelpie", "komondor", "Old English sheepdog", "Shetland sheepdog",
            "collie", "Border collie", "Bouvier des Flandres", "Rottweiler", "German shepherd",
            "Doberman", "miniature pinscher", "Greater Swiss Mountain dog", "Bernese mountain dog", "Appenzeller",
            "EntleBucher", "boxer", "bull mastiff", "Tibetan mastiff", "French bulldog",
            "Great Dane", "Saint Bernard", "Eskimo dog", "malamute", "Siberian husky",
            "affenpinscher", "basenji", "pug", "Leonberg", "Newfoundland",
            "Great Pyrenees", "Samoyed", "Pomeranian", "chow", "keeshond",
            "Brabancon griffon", "Pembroke", "Cardigan", "toy poodle", "miniature poodle",
            "standard poodle", "Mexican hairless", "timber wolf", "white wolf", "red wolf",
            "coyote", "dingo", "dhole", "African hunting dog", "hyena",
            "red fox", "kit fox", "Arctic fox", "grey fox", "tabby",
            "tiger cat", "Persian cat", "Siamese cat", "Egyptian cat", "lion",
            "tiger", "jaguar", "leopard", "snow leopard", "lynx",
            "bobcat", "clouded leopard", "sunda clouded leopard", "cheetah", "brown bear",
            "American black bear", "ice bear", "sloth bear", "mongoose", "meerkat",
            "tiger beetle", "ladybug", "ground beetle", "long-horned beetle", "leaf beetle",
            "dung beetle", "rhinoceros beetle", "weevil", "fly", "bee",
            "ant", "grasshopper", "cricket", "walking stick", "cockroach",
            "mantis", "cicada", "leafhopper", "lacewing", "dragonfly",
            "damselfly", "admiral", "ringlet", "monarch", "cabbage butterfly",
            "sulphur butterfly", "lycaenid", "starfish", "sea urchin", "sea cucumber",
            "wood rabbit", "hare", "Angora", "hamster", "porcupine",
            "fox squirrel", "marmot", "beaver", "guinea pig", "sorrel",
            "zebra", "hog", "wild boar", "warthog", "hippopotamus",
            "ox", "water buffalo", "bison", "ram", "bighorn",
            "ibex", "hartebeest", "impala", "gazelle", "Arabian camel",
            "llama", "weasel", "mink", "polecat", "black-footed ferret",
            "otter", "skunk", "badger", "armadillo", "three-toed sloth",
            "orangutan", "gorilla", "chimpanzee", "gibbon", "siamang",
            "guenon", "patas", "baboon", "macaque", "langur",
            "colobus", "proboscis monkey", "marmoset", "capuchin", "howler monkey",
            "titi", "spider monkey", "squirrel monkey", "Madagascar cat", "indri",
            "Indian elephant", "African elephant", "lesser panda", "giant panda", "barracouta",
            "eel", "coho", "rock beauty", "anemone fish", "sturgeon",
            "gar", "lionfish", "puffer", "abacus", "abaya",
            "academic gown", "accordion", "acoustic guitar", "aircraft carrier", "airliner",
            "airship", "altar", "ambulance", "amphibian", "analog clock",
            "apiary", "apron", "ashcan", "assault rifle", "backpack",
            "bakery", "balance beam", "balloon", "ballpoint", "Band Aid",
            "banjo", "bannister", "barbell", "barber chair", "barbershop",
            "barn", "barometer", "barrel", "barrow", "baseball",
            "basketball", "bassinet", "bassoon", "bathing cap", "bath towel",
            "bathtub", "beach wagon", "beacon", "beaker", "bearskin",
            "beer bottle", "beer glass", "bell cote", "bib", "bicycle-built-for-two",
            "bikini", "binder", "binoculars", "birdhouse", "boathouse",
            "bobsled", "bolo tie", "bonnet", "bookcase", "bookshop",
            "bottlecap", "bow", "bow tie", "brass", "brassiere",
            "breakwater", "breastplate", "broom", "bucket", "buckle",
            "bulletproof vest", "bullet train", "butcher shop", "cab", "caldron",
            "candle", "cannon", "canoe", "can opener", "cardigan",
            "car mirror", "carousel", "carpenter's kit", "carton", "car wheel",
            "cash machine", "cassette", "cassette player", "castle", "catamaran",
            "CD player", "cello", "cellular telephone", "chain", "chainlink fence",
            "chain mail", "chain saw", "chest", "chiffonier", "chime",
            "china cabinet", "Christmas stocking", "church", "cinema", "cleaver",
            "cliff dwelling", "cloak", "clog", "cocktail shaker", "coffee mug",
            "coffeepot", "coil", "combination lock", "computer keyboard", "confectionery",
            "container ship", "convertible", "corkscrew", "cornet", "cowboy boot",
            "cowboy hat", "cradle", "crane", "crash helmet", "crate",
            "crib", "Crock Pot", "croquet ball", "crutch", "cuirass",
            "dam", "desk", "desktop computer", "dial telephone", "diaper",
            "digital clock", "digital watch", "dining table", "dishrag", "dishwasher",
            "disk brake", "dock", "dogsled", "dome", "doormat",
            "drilling platform", "drum", "drumstick", "dumbbell", "Dutch oven",
            "electric fan", "electric guitar", "electric locomotive", "entertainment center", "envelope",
            "espresso maker", "face powder", "feather boa", "file", "fireboat",
            "fire engine", "fire screen", "flagpole", "flute", "folding chair",
            "football helmet", "forklift", "fountain", "fountain pen", "four-poster",
            "freight car", "French horn", "frying pan", "fur coat", "garbage truck",
            "gasmask", "gas pump", "goblet", "go-kart", "golf ball",
            "golfcart", "gondola", "gong", "gown", "grand piano",
            "greenhouse", "grille", "grocery store", "guillotine", "hair slide",
            "hair spray", "half track", "hammer", "hamper", "hand blower",
            "hand-held computer", "handkerchief", "hard disc", "harmonica", "harp",
            "harvester", "hatchet", "holster", "home theater", "honeycomb",
            "hook", "hoopskirt", "horizontal bar", "horse cart", "hourglass",
            "iPod", "iron", "jack-o'-lantern", "jean", "jeep",
            "jersey", "jigsaw puzzle", "jinrikisha", "joystick", "kimono",
            "knee pad", "knot", "lab coat", "ladle", "lampshade",
            "laptop", "lawn mower", "lens cap", "letter opener", "library",
            "lifeboat", "lighter", "limousine", "liner", "lipstick",
            "Loafer", "lotion", "loudspeaker", "loupe", "lumbermill",
            "magnetic compass", "mailbag", "mailbox", "maillot", "maillot",
            "manhole cover", "maraca", "marimba", "mask", "matchstick",
            "maypole", "maze", "measuring cup", "medicine chest", "megalith",
            "microphone", "microwave", "military uniform", "milk can", "minibus",
            "miniskirt", "minivan", "missile", "mitten", "mixing bowl",
            "mobile home", "Model T", "modem", "monastery", "monitor",
            "moped", "mortar", "mortarboard", "mosque", "mosquito net",
            "motor scooter", "mountain bike", "mountain tent", "mouse", "mousetrap",
            "moving van", "muzzle", "nail", "neck brace", "necklace",
            "nipple", "notebook", "obelisk", "oboe", "ocarina",
            "odometer", "oil filter", "organ", "oscilloscope", "overskirt",
            "oxcart", "oxygen mask", "packet", "paddle", "paddlewheel",
            "padlock", "paintbrush", "pajama", "palace", "panpipe",
            "paper towel", "parachute", "parallel bars", "park bench", "parking meter",
            "passenger car", "patio", "pay-phone", "pedestal", "pencil box",
            "pencil sharpener", "perfume", "Petri dish", "photocopier", "pick",
            "pickelhaube", "picket fence", "pickup", "pier", "piggy bank",
            "pill bottle", "pillow", "ping-pong ball", "pinwheel", "pirate",
            "pitcher", "plane", "planetarium", "plastic bag", "plate rack",
            "plow", "plunger", "Polaroid camera", "pole", "police van",
            "poncho", "pool table", "pop bottle", "pot", "potter's wheel",
            "power drill", "prayer rug", "printer", "prison", "projectile",
            "projector", "puck", "punching bag", "purse", "quill",
            "quilt", "racer", "racket", "radiator", "radio",
            "radio telescope", "rain barrel", "recreational vehicle", "reel", "reflex camera",
            "refrigerator", "remote control", "restaurant", "revolver", "rifle",
            "rocking chair", "rotisserie", "rubber eraser", "rugby ball", "rule",
            "running shoe", "safe", "safety pin", "saltshaker", "sandal",
            "sarong", "sax", "scabbard", "scale", "school bus",
            "schooner", "scoreboard", "screen", "screw", "screwdriver",
            "seat belt", "sewing machine", "shield", "shoe shop", "shoji",
            "shopping basket", "shopping cart", "shovel", "shower cap", "shower curtain",
            "ski", "ski mask", "sleeping bag", "slide rule", "sliding door",
            "slot", "snorkel", "snowmobile", "snowplow", "soap dispenser",
            "soccer ball", "sock", "solar dish", "sombrero", "soup bowl",
            "space bar", "space heater", "space shuttle", "spatula", "speedboat",
            "spider web", "spindle", "sports car", "spotlight", "stage",
            "steam locomotive", "steel arch bridge", "steel drum", "stethoscope", "stole",
            "stone wall", "stopwatch", "stove", "strainer", "streetcar",
            "stretcher", "studio couch", "stupa", "submarine", "suit",
            "sundial", "sunglass", "sunglasses", "sunscreen", "suspension bridge",
            "swab", "sweatshirt", "swimming trunks", "swing", "switch",
            "syringe", "table lamp", "tank", "tape player", "teapot",
            "teddy", "television", "tennis ball", "thatch", "theater curtain",
            "thimble", "thresher", "throne", "tile roof", "toaster",
            "tobacco shop", "toilet seat", "torch", "totem pole", "tow truck",
            "toyshop", "tractor", "trailer truck", "tray", "trench coat",
            "tricycle", "trimaran", "tripod", "triumphal arch", "trolleybus",
            "trombone", "tub", "turnstile", "typewriter keyboard", "umbrella",
            "unicycle", "upright", "vacuum", "vase", "vault",
            "velvet", "vending machine", "vestment", "viaduct", "violin",
            "volleyball", "waffle iron", "wall clock", "wallet", "wardrobe",
            "warplane", "washbasin", "washer", "water bottle", "water jug",
            "water tower", "whiskey jug", "whistle", "wig", "window screen",
            "window shade", "Windsor tie", "wine bottle", "wing", "wok",
            "wooden spoon", "wool", "worm fence", "wreck", "yawl",
            "yurt", "web site", "comic book", "crossword puzzle", "street sign",
            "traffic light", "book jacket", "menu", "plate", "guacamole",
            "consomme", "hot pot", "trifle", "ice cream", "ice lolly",
            "French loaf", "bagel", "pretzel", "cheeseburger", "hotdog",
            "mashed potato", "head cabbage", "broccoli", "cauliflower", "zucchini",
            "spaghetti squash", "acorn squash", "butternut squash", "cucumber", "artichoke",
            "bell pepper", "cardoon", "mushroom", "Granny Smith", "strawberry",
            "orange", "lemon", "fig", "pineapple", "banana",
            "jackfruit", "custard apple", "pomegranate", "hay", "carbonara",
            "chocolate sauce", "dough", "meat loaf", "pizza", "potpie",
            "burrito", "red wine", "espresso", "cup", "eggnog",
            "alp", "bubble", "cliff", "coral reef", "geyser",
            "lakeside", "promontory", "sandbar", "seashore", "valley",
            "volcano", "ballplayer", "groom", "scuba diver", "rapids",
            "palisade", "steamboat", "lighter", "aircraft", "liner",
            "pirate", "airliner", "airship", "balloon", "space shuttle",
            "fireboat", "gondola", "lifeboat", "canoe", "yawl",
            "catamaran", "trimaran", "container ship", "liner", "pirate",
            "aircraft carrier", "submarine", "wreck", "half track", "tank",
            "missile", "bobsled", "dogsled", "horse cart", "jinrikisha",
            "oxcart", "bicycle-built-for-two", "mountain bike", "freight car", "passenger car",
            "barrow", "shopping cart", "motor scooter", "forklift", "electric locomotive",
            "steam locomotive", "amphibian", "ambulance", "beach wagon", "cab",
            "convertible", "jeep", "limousine", "minivan", "Model T",
            "racer", "sports car", "go-kart", "golfcart", "moped",
            "police van", "moving van", "fire engine", "garbage truck", "pickup",
            "recreational vehicle", "streetcar", "trolleybus", "tractor", "mobile home",
            "trailer truck", "tow truck", "fireboat", "gondola", "lifeboat",
            "canoe", "yawl", "catamaran", "trimaran", "container ship",
            "liner", "pirate", "aircraft carrier", "submarine", "wreck"
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
            _isInitialized = false;
        }
    }
}

public sealed class ObjectDetectionResult
{
    public List<ObjectPrediction> Predictions { get; set; } = new();
}

public sealed class ObjectPrediction
{
    public int ClassIndex { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public float Confidence { get; set; }
}

