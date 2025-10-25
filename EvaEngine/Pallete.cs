using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;

namespace EvaEngine;
public static class Pallete
{
	public static RgbaFloatEva RgbaByteToRgbaFloat(this Rgba32 c)
	{
		return new(c.R, c.G, c.B, c.A);
	}
	public class Color
	{
		public RgbaFloatEva[] Colors;
	}
	public static Color[] GetColors(int tracks, Image<Rgba32> img)
	{
		Random r = new Random(0);
		double[] order = new double[tracks * 16];
		int[] coords = new int[tracks * 16];
		Color[] c = new Color[tracks];
		for (int i = 0; i < order.Length; i++)
		{
			order[i] = r.NextDouble();
			coords[i] = i;
		}
		//Array.Sort(order, coords);
		for (int i = 0; i < tracks; i++)
		{
			int h = 0;
			var k = new Color();
			c[i] = k;
			k.Colors = new RgbaFloatEva[32];
			for (int j = 0; j < 16; j++)
			{
				int y = coords[i * 16 + j];
				int x = y % 16;
				y = y - x;
				y /= 16;
				if (img.Width == 16)
				{
					k.Colors[h++] = img[x, y % img.Height].RgbaByteToRgbaFloat();
					k.Colors[h++] = img[x, y % img.Height].RgbaByteToRgbaFloat();
				}
				else
				{
					k.Colors[h++] = img[x * 2, y % img.Height].RgbaByteToRgbaFloat();
					k.Colors[h++] = img[x * 2 + 1, y % img.Height].RgbaByteToRgbaFloat();
				}
			}
		}
		return c;
	}
}