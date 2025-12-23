using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TbEinkSuperFlushTurbo
{
    public static class TestBrightness
    {
        public static void TestBrightnessCalculation()
        {
            Console.WriteLine("=== Brightness Calculation Test ===");
            
            // Create test image - pure white
            using (var whiteImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(whiteImage))
                {
                    g.Clear(Color.White);
                }
                
                float whiteBrightness = CalculateAverageBrightness(whiteImage);
                Console.WriteLine($"White Image Average Brightness: {whiteBrightness:F3} (Expected ~1.0)");
            }
            
            // Create test image - pure black
            using (var blackImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(blackImage))
                {
                    g.Clear(Color.Black);
                }
                
                float blackBrightness = CalculateAverageBrightness(blackImage);
                Console.WriteLine($"Black Image Average Brightness: {blackBrightness:F3} (Expected ~0.0)");
            }
            
            // Create test image - 50% gray
            using (var grayImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(grayImage))
                {
                    g.Clear(Color.Gray);
                }
                
                float grayBrightness = CalculateAverageBrightness(grayImage);
                Console.WriteLine($"Gray Image Average Brightness: {grayBrightness:F3} (Expected ~0.5)");
            }
            
            // Create test image - red
            using (var redImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(redImage))
                {
                    g.Clear(Color.Red);
                }
                
                float redBrightness = CalculateAverageBrightness(redImage);
                Console.WriteLine($"Red Image Average Brightness: {redBrightness:F3} (Expected ~0.299)");
            }
            
            // Create test image - green
            using (var greenImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(greenImage))
                {
                    g.Clear(Color.Green);
                }
                
                float greenBrightness = CalculateAverageBrightness(greenImage);
                Console.WriteLine($"Green Image Average Brightness: {greenBrightness:F3} (Expected ~0.587)");
            }
            
            // Create test image - blue
            using (var blueImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(blueImage))
                {
                    g.Clear(Color.Blue);
                }
                
                float blueBrightness = CalculateAverageBrightness(blueImage);
                Console.WriteLine($"Blue Image Average Brightness: {blueBrightness:F3} (Expected ~0.114)");
            }
            
            Console.WriteLine("=== Brightness Calculation Test Completed ===");
        }
        
        // Use the same brightness calculation formula as compute.hlsl
        private static float CalculateAverageBrightness(Bitmap image)
        {
            float totalBrightness = 0;
            int pixelCount = 0;
            
            BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), 
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            
            try
            {
                unsafe
                {
                    byte* ptr = (byte*)data.Scan0.ToPointer();
                    int stride = data.Stride;
                    
                    for (int y = 0; y < image.Height; y++)
                    {
                        for (int x = 0; x < image.Width; x++)
                        {
                            // BGRA format (Format32bppArgb)
                            byte b = ptr[y * stride + x * 4 + 0];
                            byte g = ptr[y * stride + x * 4 + 1];
                            byte r = ptr[y * stride + x * 4 + 2];
                            // byte a = ptr[y * stride + x * 4 + 3]; // Ignore alpha channel
                            
                            // Convert to 0-1 range
                            float rNorm = r / 255.0f;
                            float gNorm = g / 255.0f;
                            float bNorm = b / 255.0f;
                            
                            // Use standard luminance formula: 0.299*R + 0.587*G + 0.114*B
                            float luminance = 0.299f * rNorm + 0.587f * gNorm + 0.114f * bNorm;
                            
                            totalBrightness += luminance;
                            pixelCount++;
                        }
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
            
            return pixelCount > 0 ? totalBrightness / pixelCount : 0;
        }
    }
}