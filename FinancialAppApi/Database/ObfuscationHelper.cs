using System.Text;
using System.Globalization;

namespace FinancialAppApi.Database;

public static class ObfuscationHelper
{
    private static readonly string Key = "FinancialAppObfuscationKey";

    public static string Obfuscate(decimal value)
    {
        string input = value.ToString("0.00", CultureInfo.InvariantCulture);
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(bytes[i] ^ Key[i % Key.Length]);
        }
        return Convert.ToBase64String(bytes);
    }

    public static decimal Deobfuscate(string obfuscated)
    {
        if (string.IsNullOrEmpty(obfuscated)) return 0;
        try
        {
            byte[] bytes = Convert.FromBase64String(obfuscated);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(bytes[i] ^ Key[i % Key.Length]);
            }
            string decrypted = Encoding.UTF8.GetString(bytes);
            return decimal.Parse(decrypted, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}
