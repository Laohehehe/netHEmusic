using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Security;

/// <summary>
/// 敏感数据保护（非对称 + 混合加密）：
///   1) 首次运行生成 RSA-2048 密钥对；私钥用 DPAPI 再包一层后落盘（keys/session.key），公钥明文（keys/session.pub）
///   2) 加密时随机生成 AES-256 密钥 + 12 字节 Nonce，用 AES-GCM 加密正文，
///      再用 RSA-OAEP(SHA-256) 只包裹这把 AES 密钥 —— 即“RSA 包裹 + AES 正文”的混合方案
///   3) 密文格式：NMSEC1:base64( [2字节包裹长度][包裹密钥][12字节nonce][16字节tag][密文] )
/// 优点：私钥永远不会以明文出现在磁盘上；即使 config/密文被拷走，没有本机 DPAPI 也解不开。
/// </summary>
public static class SecureStore
{
    private const string BlobPrefix = "NMSEC1:";
    private static readonly object _lock = new();

    private static string _keyDir = "";
    private static string KeyFile => Path.Combine(_keyDir, "session.key");
    private static string PubFile => Path.Combine(_keyDir, "session.pub");

    public static void Init(string keyDir)
    {
        _keyDir = keyDir;
        try { Directory.CreateDirectory(_keyDir); } catch { }
        EnsureKeys();
    }

    /// <summary>确保密钥存在（不存在则生成）。</summary>
    public static void EnsureKeys()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(KeyFile) && File.Exists(PubFile)) return;
                using var rsa = RSA.Create(2048);
                // 公钥明文保存（只用于加密）
                File.WriteAllText(PubFile, rsa.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
                // 私钥先导出为 PKCS#8，再用 DPAPI(当前用户) 包一层
                var pkcs8 = rsa.ExportPkcs8PrivateKey();
                var sealedKey = ProtectedData.Protect(pkcs8, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(KeyFile, sealedKey);
                CryptographicOperations.ZeroMemory(pkcs8);
                LogManager.Log("已生成会话密钥对（RSA-2048，私钥经 DPAPI 保护）");
            }
            catch (Exception e) { LogManager.Error("生成密钥对失败: " + e.Message); }
        }
    }

    private static RSA? LoadPrivate()
    {
        try
        {
            if (!File.Exists(KeyFile)) return null;
            var sealedKey = File.ReadAllBytes(KeyFile);
            var pkcs8 = ProtectedData.Unprotect(sealedKey, null, DataProtectionScope.CurrentUser);
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            CryptographicOperations.ZeroMemory(pkcs8);
            return rsa;
        }
        catch (Exception e) { LogManager.Debug("读取私钥失败: " + e.Message); return null; }
    }

    private static RSA? LoadPublic()
    {
        try
        {
            if (!File.Exists(PubFile)) return null;
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(PubFile));
            return rsa;
        }
        catch (Exception e) { LogManager.Debug("读取公钥失败: " + e.Message); return null; }
    }

    /// <summary>加密文本 → "NMSEC1:base64"。失败时返回空串（调用方需处理）。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        lock (_lock)
        {
            try
            {
                EnsureKeys();
                using var pub = LoadPublic();
                if (pub is null) return "";
                var data = Encoding.UTF8.GetBytes(plain);
                var aesKey = RandomNumberGenerator.GetBytes(32);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[data.Length];
                var tag = new byte[16];
                using (var gcm = new AesGcm(aesKey, 16))
                    gcm.Encrypt(nonce, data, cipher, tag);
                var wrapped = pub.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
                CryptographicOperations.ZeroMemory(aesKey);

                var outBuf = new byte[2 + wrapped.Length + nonce.Length + tag.Length + cipher.Length];
                outBuf[0] = (byte)(wrapped.Length >> 8);
                outBuf[1] = (byte)(wrapped.Length & 0xFF);
                Buffer.BlockCopy(wrapped, 0, outBuf, 2, wrapped.Length);
                Buffer.BlockCopy(nonce, 0, outBuf, 2 + wrapped.Length, nonce.Length);
                Buffer.BlockCopy(tag, 0, outBuf, 2 + wrapped.Length + nonce.Length, tag.Length);
                Buffer.BlockCopy(cipher, 0, outBuf, 2 + wrapped.Length + nonce.Length + tag.Length, cipher.Length);
                return BlobPrefix + Convert.ToBase64String(outBuf);
            }
            catch (Exception e) { LogManager.Error("加密失败: " + e.Message); return ""; }
        }
    }

    /// <summary>解密 "NMSEC1:base64" → 原文；格式不符或失败返回空串。</summary>
    public static string Unprotect(string blob)
    {
        if (string.IsNullOrEmpty(blob) || !blob.StartsWith(BlobPrefix, StringComparison.Ordinal)) return "";
        lock (_lock)
        {
            try
            {
                var buf = Convert.FromBase64String(blob.Substring(BlobPrefix.Length));
                if (buf.Length < 2 + 16 + 12 + 16) return "";
                int wrappedLen = (buf[0] << 8) | buf[1];
                if (buf.Length < 2 + wrappedLen + 12 + 16) return "";
                var wrapped = new byte[wrappedLen];
                Buffer.BlockCopy(buf, 2, wrapped, 0, wrappedLen);
                var nonce = new byte[12];
                Buffer.BlockCopy(buf, 2 + wrappedLen, nonce, 0, 12);
                var tag = new byte[16];
                Buffer.BlockCopy(buf, 2 + wrappedLen + 12, tag, 0, 16);
                var cipher = new byte[buf.Length - (2 + wrappedLen + 12 + 16)];
                Buffer.BlockCopy(buf, 2 + wrappedLen + 12 + 16, cipher, 0, cipher.Length);

                using var priv = LoadPrivate();
                if (priv is null) return "";
                var aesKey = priv.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
                var plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(aesKey, 16))
                    gcm.Decrypt(nonce, cipher, tag, plain);
                CryptographicOperations.ZeroMemory(aesKey);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception e) { LogManager.Debug("解密失败: " + e.Message); return ""; }
        }
    }
}
