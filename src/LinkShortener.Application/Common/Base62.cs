namespace LinkShortener.Application.Common;

/// <summary>Alfabeto Base62: a-z, A-Z, 0-9.</summary>
public static class Base62
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private static readonly ulong Base = (ulong)Alphabet.Length; // 62
    private const uint Base62Divisor = 62u;

    /// <summary>Codifica um inteiro não-negativo em Base62 (sem padding, ordem mais-significativa primeiro).</summary>
    public static string Encode(ulong value)
    {
        if (value == 0) return Alphabet[0].ToString();

        Span<char> buffer = stackalloc char[13]; // log62(ulong.MaxValue) ~ 11 dígitos
        var index = buffer.Length;
        while (value > 0)
        {
            buffer[--index] = Alphabet[(int)(value % Base)];
            value /= Base;
        }

        return new string(buffer[index..]);
    }

    /// <summary>Decodifica uma string Base62 de volta para ulong. Lança exceção para caracteres inválidos.</summary>
    public static ulong Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            throw new FormatException("String Base62 vazia.");

        ulong result = 0;
        foreach (var c in encoded)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException($"Caractere inválido para Base62: '{c}'.");
            result = result * (ulong)Base + (ulong)digit;
        }

        return result;
    }

    /// <summary>
    /// Codifica os bytes de um hash (ex.: MD5/SHA truncado) em Base62 com tamanho fixo.
    /// Útil para gerar short codes a partir de UUIDs sem loops de busca no banco.
    /// </summary>
    public static string EncodeBytes(ReadOnlySpan<byte> bytes, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        // Interpreta os bytes como número big-endian e converte por divisão repetida.
        var digits = new List<char>(length);
        var work = bytes.ToArray();

        while (digits.Count < length)
        {
            uint remainder = 0;
            for (var i = 0; i < work.Length; i++)
            {
                uint accumulator = remainder * 256u + work[i];
                work[i] = (byte)(accumulator / Base62Divisor);
                remainder = accumulator % Base62Divisor;
            }

            digits.Add(Alphabet[remainder]);

            // Se o número zerou antes do tamanho pedido, completa com o primeiro caractere.
            bool allZero = true;
            foreach (var b in work)
                if (b != 0) { allZero = false; break; }
            if (allZero && digits.Count < length)
            {
                while (digits.Count < length) digits.Add(Alphabet[0]);
                break;
            }
        }

        digits.Reverse();
        return new string(digits.ToArray());
    }
}
