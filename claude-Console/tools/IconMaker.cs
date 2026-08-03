using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeConsole.Tools
{
    internal static class IconMaker
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static int Main(string[] args)
        {
            if (args.Length != 1) return 2;

            using (Bitmap bitmap = new Bitmap(64, 64))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(5, 5, 54, 54),
                    Color.FromArgb(238, 129, 92),
                    Color.FromArgb(206, 83, 63),
                    45f))
                {
                    graphics.FillEllipse(brush, 5, 5, 54, 54);
                }

                using (Pen ring = new Pen(Color.FromArgb(238, 255, 255, 255), 4f))
                {
                    ring.StartCap = LineCap.Round;
                    ring.EndCap = LineCap.Round;
                    graphics.DrawArc(ring, 16, 16, 32, 32, -90, 250);
                }

                using (SolidBrush dot = new SolidBrush(Color.White))
                {
                    graphics.FillEllipse(dot, 42, 37, 8, 8);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle))
                    using (FileStream stream = new FileStream(args[0], FileMode.Create, FileAccess.Write))
                    {
                        icon.Save(stream);
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }

            return 0;
        }
    }
}
