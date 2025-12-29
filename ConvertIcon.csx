using System.Drawing;
using System.Drawing.Imaging;

// Simple utility to convert PNG to ICO
var pngPath = args.Length > 0 ? args[0] : "Resources/icon.png";
var icoPath = args.Length > 1 ? args[1] : "Resources/icon.ico";

Console.WriteLine($"Converting {pngPath} to {icoPath}...");

using var bitmap = new Bitmap(pngPath);

// Resize to 32x32 for tray icon
using var icon32 = new Bitmap(bitmap, new Size(32, 32));

using var fs = new FileStream(icoPath, FileMode.Create);
using var bw = new BinaryWriter(fs);

// ICO header
bw.Write((short)0); // Reserved
bw.Write((short)1); // Type: Icon
bw.Write((short)1); // Number of images

// Image directory
bw.Write((byte)32); // Width
bw.Write((byte)32); // Height
bw.Write((byte)0);  // Color palette
bw.Write((byte)0);  // Reserved
bw.Write((short)1); // Color planes
bw.Write((short)32); // Bits per pixel
bw.Write(0);        // Image size (placeholder)
bw.Write(22);       // Image offset

// Save PNG data
using var ms = new MemoryStream();
icon32.Save(ms, ImageFormat.Png);
var pngData = ms.ToArray();

// Update image size
fs.Seek(14, SeekOrigin.Begin);
bw.Write(pngData.Length);

// Write PNG data
fs.Seek(22, SeekOrigin.Begin);
bw.Write(pngData);

Console.WriteLine($"Icon created successfully: {icoPath}");
Console.WriteLine($"File size: {new FileInfo(icoPath).Length} bytes");
