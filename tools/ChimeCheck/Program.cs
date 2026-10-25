using System;
internal static class ChimeCheck
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var s = BuildReminderChime();
        using var f = System.IO.File.Create(args[0]);
        s.CopyTo(f);
    }

    public static Stream BuildReminderChime()
    {
        const int rate = 44100;
        const double cycleSeconds = 2.4;
        var total = (int)(rate * cycleSeconds);
        var samples = new short[total];

        // (起始秒, 时长秒, 频率Hz)：A5 → C#6 → E6，清亮但不刺耳
        (double Start, double Duration, double Freq)[] notes =
        [
            (0.00, 0.34, 880.00),
            (0.40, 0.34, 1108.73),
            (0.80, 0.55, 1318.51)
        ];

        foreach (var note in notes)
        {
            var offset = (int)(note.Start * rate);
            var length = (int)(note.Duration * rate);
            for (var i = 0; i < length && offset + i < total; i++)
            {
                var t = (double)i / rate;
                var attack = Math.Min(1.0, t / 0.012);           // 12ms 起音，避免爆音
                var decay = Math.Exp(-3.2 * t / note.Duration);   // 指数衰减，听感更像敲击
                var wave = Math.Sin(2 * Math.PI * note.Freq * t) * 0.72
                         + Math.Sin(2 * Math.PI * note.Freq * 2 * t) * 0.18;   // 一点二次谐波
                var value = samples[offset + i] + (int)(wave * attack * decay * 0.50 * short.MaxValue);
                samples[offset + i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);      // PCM
            writer.Write((short)1);      // 单声道
            writer.Write(rate);
            writer.Write(rate * 2);      // 每秒字节数
            writer.Write((short)2);      // 块对齐
            writer.Write((short)16);     // 位深
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (var sample in samples) writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }
}
