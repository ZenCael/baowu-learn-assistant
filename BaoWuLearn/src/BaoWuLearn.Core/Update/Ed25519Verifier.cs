using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace BaoWuLearn.Core.Update;

/// <summary>
/// 更新清单的 ed25519 验签。
///
/// 为什么要签名（而不是只校 sha256）：sha256 只能防下载损坏，防不了镜像投毒 ——
/// 公共 gh 代理这类中转节点可以连"最新版本号"一起伪造。签名把信任与传输解耦：
/// 私钥只在发布机上，任何中转节点被污染都伪造不了新版本、也伪装不了旧版本
///（配合清单里的 pubDate 单调递增，连降级攻击一起挡掉）。
///
/// 公钥内嵌在代码里（raw 32 字节 hex）。轮换密钥 = 换这个常量 + 发一版客户端，
/// 所以发布私钥丢了/泄了都必须换钥发版，不能只用旧钥续签。
/// </summary>
public static class Ed25519Verifier
{
    /// <summary>
    /// 发布清单的验签公钥（ed25519 raw 32 字节，小写 hex）。
    /// 对应 <c>build/keys/update-ed25519.pem</c>（该文件被 gitignore 挡住、绝不入库）。
    /// </summary>
    public const string PublicKeyHex =
        "0f9a714288922af13bec6942d036cdc67ea291dd5a7d55555be742a111489bf3";

    /// <summary>
    /// 验一条 detached 签名。用调用方给的公钥（自检拿 RFC 8032 官方向量测验签器本身，
    /// 业务路径则用 <see cref="PublicKeyHex"/>）。
    /// </summary>
    public static bool Verify(byte[] publicKeyRaw32, byte[] data, byte[] signature)
    {
        if (publicKeyRaw32.Length != 32 || signature.Length != 64) return false;
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKeyRaw32, 0));
            signer.BlockUpdate(data, 0, data.Length);
            return signer.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用内嵌发布公钥验 <paramref name="data"/> 的签名（hex 编码，容忍大小写与空白）。
    /// </summary>
    public static bool VerifyWithReleaseKey(byte[] data, string signatureHex)
    {
        var pub = Convert.FromHexString(PublicKeyHex);
        byte[] sig;
        try
        {
            sig = Convert.FromHexString(signatureHex.Trim());
        }
        catch
        {
            return false; // 非 hex，直接判失败
        }
        return Verify(pub, data, sig);
    }
}
