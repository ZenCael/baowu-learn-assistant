using System.Text;
using Org.BouncyCastle.Asn1.GM;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace BaoWuLearn.Core.Crypto;

/// <summary>
/// SM2 国密加密。
///
/// 输出格式严格对齐网页端 <c>sm-crypto</c> 的 <c>sm2.doEncrypt(plain, pubKey, 1)</c>：
///   1) 密文排列为 <b>C1C3C2</b>（即 cipherMode = 1）
///   2) C1 点 <b>不带 04 前缀</b>（仅 X‖Y，共 64 字节）
///   3) 输出为<b>小写 hex 字符串</b>
///
/// 这三条是与 BouncyCastle 默认行为的三处经典差异，缺一不可，否则服务端解密必然失败。
/// </summary>
public static class Sm2Crypto
{
    /// <summary>
    /// 平台前端硬编码的 SM2 公钥（Base64 编码，解码后为标准的 04‖X‖Y 未压缩点，共 65 字节）。
    /// 来源：平台前端 JS bundle 静态提取。
    /// </summary>
    public const string PlatformPublicKey =
        "BJeYoHWNsf60Vr2wPJWEWRvjH6m5r/JvK7Pww8SdohnwAkHKVy0tikYYOYmuKhR83BUS+duMyjAbVtyXZTfc+jY=";

    private const string CurveName = "sm2p256v1";

    /// <summary>
    /// 使用平台公钥加密明文，返回与网页端一致的小写 hex 密文。
    /// 后续需作为 loginName / password / mobile 等字段的值提交。
    /// 注意：图形验证码字段（captchaCode）**不加密**，网页端也是明文透传。
    /// </summary>
    public static string Encrypt(string plain, string publicKeyBase64 = PlatformPublicKey)
    {
        ArgumentNullException.ThrowIfNull(plain);

        var pubBytes = Convert.FromBase64String(publicKeyBase64);
        var output = EncryptToBytes(plain, pubBytes);
        return Convert.ToHexString(output).ToLowerInvariant();
    }

    /// <summary>加密并返回原始字节（C1 已裁去 04 前缀）。</summary>
    public static byte[] EncryptToBytes(string plain, byte[] publicKeyBytes)
    {
        var curve = GMNamedCurves.GetByName(CurveName);
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var point = curve.Curve.DecodePoint(publicKeyBytes);
        var publicParams = new ECPublicKeyParameters(point, domain);

        var engine = new SM2Engine(SM2Engine.Mode.C1C3C2);
        engine.Init(true, new ParametersWithRandom(publicParams, new SecureRandom()));

        var input = Encoding.UTF8.GetBytes(plain);
        var cipher = engine.ProcessBlock(input, 0, input.Length);

        // BouncyCastle 输出：C1(65，含 04) ‖ C3(32) ‖ C2
        // 目标格式：      C1(64，去 04) ‖ C3(32) ‖ C2
        if (cipher.Length > 0 && cipher[0] == 0x04)
        {
            var trimmed = new byte[cipher.Length - 1];
            Buffer.BlockCopy(cipher, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        return cipher;
    }

    /// <summary>
    /// 用私钥解密（仅用于本地自检：验证加解密往返是否自洽）。
    /// 注意：密文需接受带或不带 04 前缀两种形式。
    /// </summary>
    public static string Decrypt(string cipherHex, string privateKeyHex)
    {
        var cipher = Convert.FromHexString(cipherHex);
        var priv = Convert.FromHexString(privateKeyHex);

        var curve = GMNamedCurves.GetByName(CurveName);
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var d = new Org.BouncyCastle.Math.BigInteger(1, priv);
        var privateParams = new ECPrivateKeyParameters(d, domain);

        // 还原 04 前缀（BouncyCastle 解析 C1 需要）
        if (cipher[0] != 0x04)
        {
            var restored = new byte[cipher.Length + 1];
            restored[0] = 0x04;
            Buffer.BlockCopy(cipher, 0, restored, 1, cipher.Length);
            cipher = restored;
        }

        var engine = new SM2Engine(SM2Engine.Mode.C1C3C2);
        engine.Init(false, privateParams);
        var plain = engine.ProcessBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// 生成本地自检用的密钥对（仅测试）。
    /// </summary>
    public static (string PublicKeyBase64, string PrivateKeyHex) GenerateKeyPair()
    {
        var curve = GMNamedCurves.GetByName(CurveName);
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(domain, new SecureRandom()));
        var pair = generator.GenerateKeyPair();

        var pub = (ECPublicKeyParameters)pair.Public;
        var priv = (ECPrivateKeyParameters)pair.Private;
        return (
            Convert.ToBase64String(pub.Q.GetEncoded(false)),
            priv.D.ToString(16).ToLowerInvariant().PadLeft(64, '0'));
    }
}
