using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace ClashTrafficSentinel.Tools
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
                using (LinearGradientBrush background = new LinearGradientBrush(
                    new Rectangle(3, 3, 58, 58), Color.FromArgb(7, 29, 35), Color.FromArgb(15, 67, 63), 45f))
                using (GraphicsPath tile = RoundedRectangle(new Rectangle(3, 3, 58, 58), 15))
                    graphics.FillPath(background, tile);

                Color accent = Color.FromArgb(67, 231, 178);
                using (Pen ring = new Pen(Color.FromArgb(220, accent), 3.2f))
                using (Pen sweep = new Pen(Color.FromArgb(255, accent), 4f))
                {
                    ring.StartCap = LineCap.Round;
                    ring.EndCap = LineCap.Round;
                    sweep.StartCap = LineCap.Round;
                    sweep.EndCap = LineCap.Round;
                    graphics.DrawEllipse(ring, 14, 14, 36, 36);
                    graphics.DrawEllipse(ring, 23, 23, 18, 18);
                    graphics.DrawLine(sweep, 32, 32, 46, 21);
                    graphics.DrawArc(sweep, 11, 11, 42, 42, 292, 43);
                }
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 184, 77)))
                    graphics.FillEllipse(dot, 43, 17, 8, 8);
                using (SolidBrush center = new SolidBrush(Color.White))
                    graphics.FillEllipse(center, 29, 29, 6, 6);

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle))
                    using (FileStream stream = new FileStream(args[0], FileMode.Create, FileAccess.Write))
                        icon.Save(stream);
                }
                finally { DestroyIcon(handle); }
            }
            return 0;
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

