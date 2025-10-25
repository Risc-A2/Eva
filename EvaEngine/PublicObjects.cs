namespace EvaEngine;

public static class PublicObjects
{
	public static string FormatSmart(this TimeSpan timeSpan, bool includeMilliseconds = true)
	{
		int totalHours = (int)timeSpan.TotalHours;
		int minutes = timeSpan.Minutes;
		int seconds = timeSpan.Seconds;
		int milliseconds = timeSpan.Milliseconds;

		// Determinar qué componentes mostrar
		bool showHours = totalHours > 0;
		bool showMinutes = showHours || minutes > 0;
		bool showSeconds = true; // Siempre mostrar segundos
		bool showMilliseconds = includeMilliseconds && milliseconds > 0;

		// Construir la cadena de formato dinámicamente
		string format = "";
		if (showHours)
		{
			format += $"{totalHours}:";
		}
        
		if (showMinutes)
		{
			format += showHours ? $"{minutes:D2}:" : $"{minutes}:";
		}
		else if (showHours)
		{
			format += "00:"; // Si mostramos horas pero no minutos
		}

		format += showMinutes ? $"{seconds:D2}" : $"{seconds}";

		if (showMilliseconds)
		{
			format += $".{milliseconds:D3}";
		}

		return format;
	}

	static int GetCpuCores()
	{
		if (OperatingSystem.IsLinux())
		{
			StreamReader reader = new("/proc/cpuinfo");
			string? huh = reader.ReadLine();
			while (huh != null)
			{
				if (huh.Contains("cores"))
				{
					string[] Split = huh.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					int cores = int.Parse(Split[^1]);
					reader.Close();
					reader.Dispose();
					return cores;
				}
				huh = reader.ReadLine();
			}
			reader.Close();
			reader.Dispose();
		}
		return Environment.ProcessorCount;
	}
	public static ParallelOptions opts = new ParallelOptions()
	{
		MaxDegreeOfParallelism = GetCpuCores()
	};
	public static readonly string[] SizeSuffixes = 
		{ "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
	public static string SizeSuffix(Int64 value, int decimalPlaces = 2)
	{
		if (decimalPlaces < 0) { throw new ArgumentOutOfRangeException(nameof(decimalPlaces)); }
		if (value < 0) { return "-" + SizeSuffix(-value, decimalPlaces); } 
		if (value == 0) { return string.Format("{0:n" + decimalPlaces + "} bytes", 0); }

		// mag is 0 for bytes, 1 for KB, 2, for MB, etc.
		int mag = (int)Math.Log(value, 1024);

		// 1L << (mag * 10) == 2 ^ (10 * mag) 
		// [i.e. the number of bytes in the unit corresponding to mag]
		decimal adjustedSize = (decimal)value / (1L << (mag * 10));

		// make adjustment when the value is large enough that
		// it would round up to 1000 or more
		if (Math.Round(adjustedSize, decimalPlaces) >= 1000)
		{
			mag += 1;
			adjustedSize /= 1024;
		}

		return string.Format("{0:n" + decimalPlaces + "} {1}", 
			adjustedSize, 
			SizeSuffixes[mag]);
	}
}