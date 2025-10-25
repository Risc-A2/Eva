using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EvaEngine;
[StructLayout(LayoutKind.Sequential)]
public struct RgbaFloatEva
{
	private Vector4 _vec;
	
	public float R
	{
		get => _vec.X;
		set => _vec.X = value;
	}

	public float G
	{
		get => _vec.Y;
		set => _vec.Y = value;
	}

	public float B
	{
		get => _vec.Z;
		set => _vec.Z = value;
	}

	public float A
	{
		get => _vec.W;
		set => _vec.W = value;
	}

	public RgbaFloatEva(float r, float g, float b, float a)
	{
		_vec = new Vector4(r, g, b, a);
	}
	public RgbaFloatEva(byte r, byte g, byte b, byte a)
	{
		_vec = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
	}

	public RgbaFloatEva(Vector4 vec)
	{
		_vec = vec;
	}

	// Para interoperar con Veldrid
	public static implicit operator Veldrid.RgbaFloat(RgbaFloatEva c)
	{
		return new Veldrid.RgbaFloat(c._vec);
	}

	public static explicit operator RgbaFloatEva(Veldrid.RgbaFloat c)
	{
		return new RgbaFloatEva(c.ToVector4());
	}

	public static explicit operator RgbaFloatEva(Vector4 vec)
	{
		return new RgbaFloatEva(vec);
	}

	public Vector4 ToVector4() => _vec;

	public override string ToString() => $"R:{R} G:{G} B:{B} A:{A}";
}