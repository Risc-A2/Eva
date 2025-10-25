using System.Reflection;

namespace EvaEngine;

public class ResourceExtractor
{
	public static Stream Read(string folder, string file)
	{
		var asm = typeof(ResourceExtractor).Assembly;

		string resourceName = typeof(ResourceExtractor).Namespace + "." +
							  (string.IsNullOrEmpty(folder) ? "" : folder + ".") + file;

		var stream = asm.GetManifestResourceStream(resourceName);

		if (stream == null)
			throw new FileNotFoundException("No se pudo encontrar el recurso embebido: " + resourceName);

		return stream;
	}
	public static byte[] ReadAsByte(string folder, string file)
	{
		var asm = typeof(ResourceExtractor).Assembly;

		string resourceName = typeof(ResourceExtractor).Namespace + "." +
		                      (string.IsNullOrEmpty(folder) ? "" : folder + ".") + file;

		var stream = asm.GetManifestResourceStream(resourceName);

		if (stream == null)
			throw new FileNotFoundException("No se pudo encontrar el recurso embebido: " + resourceName);
		MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

}