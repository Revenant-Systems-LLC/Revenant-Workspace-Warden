using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Tesseract;

namespace RevenantWorkspaceWarden
{
    /// <summary>
    /// Handles active-window screen capture and Tesseract OCR preprocessing.
    /// </summary>
    internal sealed class OcrService
    {
        private readonly IWardenHost _host;

        public OcrService(IWardenHost host)
        {
            _host = host;
        }

        public async Task CaptureActiveWindowForTutorAsync()
        {
            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    _host.AddSystemMessage("[Screen] No active window found.");
                    return;
                }

                // Window title
                var titleBuilder = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                string windowTitle = titleBuilder.ToString();

                // Client area bounds (excludes title bar / borders — cleaner for OCR)
                if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT clientRect))
                {
                    _host.AddSystemMessage("[Screen] Could not get client area.");
                    return;
                }

                var clientTopLeft = new NativeMethods.POINT { X = clientRect.Left, Y = clientRect.Top };
                NativeMethods.ClientToScreen(hwnd, ref clientTopLeft);

                int width  = clientRect.Right  - clientRect.Left;
                int height = clientRect.Bottom - clientRect.Top;

                if (width <= 0 || height <= 0)
                {
                    _host.AddSystemMessage("[Screen] Invalid window client size.");
                    return;
                }

                // Capture client area
                using var bitmap = new System.Drawing.Bitmap(
                    width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        clientTopLeft.X, clientTopLeft.Y, 0, 0,
                        new System.Drawing.Size(width, height),
                        System.Drawing.CopyPixelOperation.SourceCopy);
                }

                // Save capture
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string captureDir = Path.Combine(appData, "RevenantWorkspaceWarden", "screen_captures");
                Directory.CreateDirectory(captureDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string imagePath = Path.Combine(captureDir, $"screen_{timestamp}.png");
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);

                _host.AddSystemMessage($"[Screen] Captured active window: {windowTitle}");
                _host.AddSystemMessage($"[Screen] Image saved: {imagePath}");

                // OCR
                string extractedText = "";
                try
                {
                    string tessDataPath = EnsureTesseractData(appData);

                    if (Directory.Exists(tessDataPath) &&
                        File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                    {
                        using var processedBitmap = PreprocessForOcr(bitmap);
                        using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                        engine.SetVariable("psm", "6");
                        engine.SetVariable("oem", "3");

                        using var pix  = Pix.LoadFromMemory(BitmapToByteArray(processedBitmap));
                        using var page = engine.Process(pix);
                        extractedText  = page.GetText().Trim();
                    }
                    else
                    {
                        _host.AddSystemMessage(
                            "[Screen] Tesseract eng.traineddata not found. OCR disabled. " +
                            "Drop eng.traineddata in your Downloads folder and try again.");
                    }
                }
                catch (Exception ocrEx)
                {
                    _host.AddSystemMessage($"[Screen] OCR error: {ocrEx.Message}");
                }

                // Build context prompt
                string contextPrompt =
                    "The user is currently in class on maestro.org. They just triggered a screen capture of the active browser window.\n" +
                    $"Window title: {windowTitle}\n\n" +
                    "Below is text extracted via OCR from what is currently visible on their screen (likely lecture content, code examples, or assignment instructions).\n\n" +
                    "Please analyze this as class material. Be helpful for someone actively learning.\n\n";

                contextPrompt += string.IsNullOrWhiteSpace(extractedText)
                    ? "No text could be extracted via OCR. A screenshot was saved to the screen_captures folder if you want to reference it."
                    : "=== OCR EXTRACTED TEXT FROM SCREEN ===\n\n" + extractedText;

                _host.AddSystemMessage("[Screen] Sending to tutor...");
                await _host.DispatchLlmAsync(contextPrompt, forceTutor: _host.IsTutorMode);
            }
            catch (Exception ex)
            {
                _host.AddSystemMessage($"[Screen] Capture failed: {ex.Message}");
            }
        }

        // ── OCR Preprocessing ─────────────────────────────────────────────────────

        /// <summary>
        /// Converts to grayscale and boosts contrast using a ColorMatrix — orders of magnitude
        /// faster than the previous pixel-by-pixel GetPixel/SetPixel loop on large screens.
        /// </summary>
        private static System.Drawing.Bitmap PreprocessForOcr(System.Drawing.Bitmap original)
        {
            var result = new System.Drawing.Bitmap(original.Width, original.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using var g = System.Drawing.Graphics.FromImage(result);

            // Grayscale + slight contrast boost
            var colorMatrix = new System.Drawing.Imaging.ColorMatrix(new float[][]
            {
                new float[] { 0.30f,  0.30f,  0.30f,  0f, 0f },
                new float[] { 0.59f,  0.59f,  0.59f,  0f, 0f },
                new float[] { 0.11f,  0.11f,  0.11f,  0f, 0f },
                new float[] { 0f,     0f,     0f,     1f, 0f },
                new float[] { 0.05f,  0.05f,  0.05f,  0f, 1f }  // slight brightness offset
            });

            var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(colorMatrix);

            g.DrawImage(original,
                new System.Drawing.Rectangle(0, 0, original.Width, original.Height),
                0, 0, original.Width, original.Height,
                System.Drawing.GraphicsUnit.Pixel,
                attributes);

            return result;
        }

        private static byte[] BitmapToByteArray(System.Drawing.Bitmap bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        // ── Tesseract Data Setup ──────────────────────────────────────────────────

        /// <summary>
        /// Ensures eng.traineddata is in the app's tessdata folder.
        /// Searches common locations and copies it on first run.
        /// F:\Downloads is gone — uses portable Environment paths only.
        /// </summary>
        private string EnsureTesseractData(string appData)
        {
            string targetTessData = Path.Combine(appData, "RevenantWorkspaceWarden", "tessdata");
            string targetFile     = Path.Combine(targetTessData, "eng.traineddata");

            if (File.Exists(targetFile))
                return targetTessData;

            string downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            var searchPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata"),
                Path.Combine(appData, "RevenantWorkspaceWarden", "tessdata"),
                downloadsPath,
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            foreach (var dir in searchPaths)
            {
                string candidate = Path.Combine(dir, "eng.traineddata");
                if (File.Exists(candidate))
                {
                    Directory.CreateDirectory(targetTessData);
                    File.Copy(candidate, targetFile, overwrite: true);
                    _host.AddSystemMessage(
                        "[Screen] Found eng.traineddata and set up OCR. Press F2 to use it.");
                    return targetTessData;
                }
            }

            // Not found — tell the user exactly where to put it
            Directory.CreateDirectory(targetTessData);
            _host.AddSystemMessage(
                $"[Screen] OCR data not found.\n\nDrop eng.traineddata here:\n{targetFile}\n\nThen press F2.");
            return targetTessData;
        }
    }
}
