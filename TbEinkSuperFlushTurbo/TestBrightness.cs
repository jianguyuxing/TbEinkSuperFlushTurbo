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
            Console.WriteLine("=== 测试亮度计算功能 ===");
            
            // 创建测试图像 - 纯白色
            using (var whiteImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(whiteImage))
                {
                    g.Clear(Color.White);
                }
                
                float whiteBrightness = CalculateAverageBrightness(whiteImage);
                Console.WriteLine($"白色图像平均亮度: {whiteBrightness:F3} (期望接近1.0)");
            }
            
            // 创建测试图像 - 纯黑色
            using (var blackImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(blackImage))
                {
                    g.Clear(Color.Black);
                }
                
                float blackBrightness = CalculateAverageBrightness(blackImage);
                Console.WriteLine($"黑色图像平均亮度: {blackBrightness:F3} (期望接近0.0)");
            }
            
            // 创建测试图像 - 50%灰色
            using (var grayImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(grayImage))
                {
                    g.Clear(Color.Gray);
                }
                
                float grayBrightness = CalculateAverageBrightness(grayImage);
                Console.WriteLine($"灰色图像平均亮度: {grayBrightness:F3} (期望接近0.5)");
            }
            
            // 创建测试图像 - 红色
            using (var redImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(redImage))
                {
                    g.Clear(Color.Red);
                }
                
                float redBrightness = CalculateAverageBrightness(redImage);
                Console.WriteLine($"红色图像平均亮度: {redBrightness:F3} (期望接近0.299)");
            }
            
            // 创建测试图像 - 绿色
            using (var greenImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(greenImage))
                {
                    g.Clear(Color.Green);
                }
                
                float greenBrightness = CalculateAverageBrightness(greenImage);
                Console.WriteLine($"绿色图像平均亮度: {greenBrightness:F3} (期望接近0.587)");
            }
            
            // 创建测试图像 - 蓝色
            using (var blueImage = new Bitmap(100, 100))
            {
                using (var g = Graphics.FromImage(blueImage))
                {
                    g.Clear(Color.Blue);
                }
                
                float blueBrightness = CalculateAverageBrightness(blueImage);
                Console.WriteLine($"蓝色图像平均亮度: {blueBrightness:F3} (期望接近0.114)");
            }
            
            Console.WriteLine("=== 亮度计算测试完成 ===");
        }
        
        // 使用与compute.hlsl中相同的亮度计算公式
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
                            // BGRA格式 (Format32bppArgb)
                            byte b = ptr[y * stride + x * 4 + 0];
                            byte g = ptr[y * stride + x * 4 + 1];
                            byte r = ptr[y * stride + x * 4 + 2];
                            // byte a = ptr[y * stride + x * 4 + 3]; // 忽略alpha通道
                            
                            // 转换为0-1范围
                            float rNorm = r / 255.0f;
                            float gNorm = g / 255.0f;
                            float bNorm = b / 255.0f;
                            
                            // 使用标准亮度公式：0.299*R + 0.587*G + 0.114*B
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