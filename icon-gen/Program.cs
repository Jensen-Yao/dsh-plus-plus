using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

// icon-gen <in.png> <out.ico> [scale] [preview.png]
// Renders the source artwork scaled down (default 0.72) on a background sampled
// from the source's border, and packs 16/24/32/48/64/128/256 PNG entries into an ICO.

if (args.Length < 2)
{
    Console.WriteLine("usage: icon-gen <in.png> <out.ico> [scale] [preview.png]");
    return 1;
}
var src = args[0];
var dst = args[1];
var scale = args.Length > 2 && double.TryParse(args[2], out var s) ? s : 0.72;
var preview = args.Length > 3 ? args[3] : null;

using var original = new Bitmap(src);

Color borderColor = SampleBorder(original);

int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
var pngs = new List<byte[]>();
foreach (var size in sizes)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(borderColor);
        var target = Math.Max(1, (int)Math.Round(size * scale));
        var off = (size - target) / 2;
        g.DrawImage(original, new Rectangle(off, off, target, target),
            new Rectangle(0, 0, original.Width, original.Height), GraphicsUnit.Pixel);
    }
    using var pms = new MemoryStream();
    bmp.Save(pms, ImageFormat.Png);
    pngs.Add(pms.ToArray());
    if (size == 256 && preview != null)
    {
        bmp.Save(preview, ImageFormat.Png);
    }
}

using var ms = new MemoryStream();
using var bw = new BinaryWriter(ms);
bw.Write((short)0);              // reserved
bw.Write((short)1);              // type: icon
bw.Write((short)sizes.Length);   // count
int offset = 6 + 16 * sizes.Length;
for (int i = 0; i < sizes.Length; i++)
{
    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // width (0 = 256)
    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // height
    bw.Write((byte)0);            // palette
    bw.Write((byte)0);            // reserved
    bw.Write((short)1);           // planes
    bw.Write((short)32);          // bpp
    bw.Write(pngs[i].Length);
    bw.Write(offset);
    offset += pngs[i].Length;
}
foreach (var png in pngs) bw.Write(png);
File.WriteAllBytes(dst, ms.ToArray());
Console.WriteLine($"icon written: {dst} ({sizes.Length} sizes, scale={scale:0.##}, bg=#{borderColor.R:X2}{borderColor.G:X2}{borderColor.B:X2})");
return 0;

static Color SampleBorder(Bitmap bmp)
{
    int r = 0, g = 0, bl = 0, n = 0;
    var pts = new List<(int x, int y)>();
    for (int x = 8; x < bmp.Width; x += 64) { pts.Add((x, 4)); pts.Add((x, bmp.Height - 5)); }
    for (int y = 8; y < bmp.Height; y += 64) { pts.Add((4, y)); pts.Add((bmp.Width - 5, y)); }
    foreach (var (x, y) in pts)
    {
        var c = bmp.GetPixel(x, y);
        r += c.R; g += c.G; bl += c.B; n++;
    }
    return Color.FromArgb(r / n, g / n, bl / n);
}
