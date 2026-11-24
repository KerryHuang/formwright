using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DynamicModules.Domain.Accounts;

/// <summary>
/// RFC 6238 TOTP（帳號安全設計 §8.1、T7）：HMAC-SHA1、6 位數、30 秒一步、容許前後各一步。
/// 演算法、位數、步長、容許步數是技術限制（主流驗證器 App 相容，§22），不做成政策。
/// </summary>
public static class Totp
{
    public const int Digits = 6, StepSeconds = 30, Window = 1;
    /// <summary>金鑰長度 20 bytes（§22 安全檢查）。</summary>
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>時間步：Unix 秒數除以 30（RFC 6238 §4.2，T0 = 0）。</summary>
    public static long StepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>指定時間步的碼（RFC 4226 動態截斷）；<paramref name="digits"/> 只給測試向量用 8 位。</summary>
    public static string Compute(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var mod = 1;
        for (var i = 0; i < digits; i++) mod *= 10;
        return (binary % mod).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>輸入去掉空白後是 6 位數字。</summary>
    public static string? Normalize(string? input)
    {
        var s = (input ?? "").Replace(" ", "", StringComparison.Ordinal).Trim();
        return s.Length == Digits && s.All(char.IsAsciiDigit) ? s : null;
    }

    /// <summary>
    /// 比對：時間步在前後各一步內、而且大於 <paramref name="lastStep"/>（重放：同一步或更早的碼一律拒絕，RFC 6238 §5.2）。
    /// 符合回該時間步；不符回 null。每一步都用固定時間比較，不因提早命中而少算。
    /// </summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now, long? lastStep)
    {
        var normalized = Normalize(code);
        var input = Encoding.ASCII.GetBytes(normalized ?? new string('x', Digits));
        var current = StepOf(now);
        long? matched = null;
        for (var step = current - Window; step <= current + Window; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(input, Encoding.ASCII.GetBytes(Compute(secret, step)));
            if (equal && normalized is not null && (lastStep is null || step > lastStep) && matched is null) matched = step;
        }
        return matched;
    }

    /// <summary>
    /// 驗證器 App 的設定網址：<c>otpauth://totp/{issuer}:{account}?secret=...&amp;issuer={issuer}</c>，label 與 issuer 都做 URL 編碼（§8.1）。
    /// </summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret)
    {
        var i = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{i}:{Uri.EscapeDataString(account)}?secret={Base32.Encode(secret)}&issuer={i}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}

/// <summary>RFC 4648 Base32（大寫、不補 <c>=</c>）：TOTP 金鑰的顯示與復原碼字元集。</summary>
public static class Base32
{
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>忽略大小寫、空白、連字號與結尾的 <c>=</c>；有其他字元回 null。</summary>
    public static byte[]? Decode(string? text)
    {
        if (text is null) return null;
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '=') continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0) return null;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}

/// <summary>
/// 復原碼（§8.4、T10、T24）：10 組、每組 16 個 Base32 字元，顯示成 <c>ABCD-EFGH-JKLM-NPQR</c>。
/// 存 SHA-256，雜湊的輸入是**正規化後的 16 個大寫 Base32 字元**（去連字號與空白、轉大寫）；產生與比對共用 <see cref="Hash"/>，
/// 這是給備份還原（bk1）的契約（§19），不得改成加密存放、也不得改正規化方式。
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10, Length = 16, RemindAtOrBelow = 2;

    /// <summary>一組新的顯示用復原碼（含連字號）。</summary>
    public static string NewCode()
    {
        var raw = RandomNumberGenerator.GetString(Base32.Alphabet, Length);
        return Format(raw);
    }

    public static IReadOnlyList<string> NewSet() => Enumerable.Range(0, Count).Select(_ => NewCode()).ToList();

    public static string Format(string normalized) =>
        string.Join('-', Enumerable.Range(0, normalized.Length / 4).Select(i => normalized.Substring(i * 4, 4)));

    /// <summary>去掉空白與連字號、轉大寫；不是 16 個 Base32 字元回 null。</summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;
        var sb = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (c is ' ' or '-' or '\t') continue;
            var upper = char.ToUpperInvariant(c);
            if (!Base32.Alphabet.Contains(upper)) return null;
            sb.Append(upper);
            if (sb.Length > Length) return null;
        }
        return sb.Length == Length ? sb.ToString() : null;
    }

    /// <summary>正規化後的 SHA-256（小寫十六進位）；輸入不合格式回 null。</summary>
    public static string? Hash(string? input) =>
        Normalize(input) is { } n ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(n))).ToLowerInvariant() : null;
}
