using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IControlPrint;

/// <summary>Token do servidor protegido com DPAPI (escopo LocalMachine): só esta máquina consegue ler.</summary>
public sealed class TokenStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("iControlPrint|v1");

    public sealed record Pairing(string Token, string ServerId, string Channel, string CompanyId, string CompanyName, string Cnpj);

    public Pairing? Load()
    {
        if (!File.Exists(Paths.Token)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(Paths.Token), Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<Pairing>(raw);
        }
        catch { return null; }
    }

    public void Save(Pairing p)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(p);
        var enc = ProtectedData.Protect(raw, Entropy, DataProtectionScope.LocalMachine);
        var tmp = Paths.Token + ".tmp";
        File.WriteAllBytes(tmp, enc);
        File.Move(tmp, Paths.Token, true);
    }

    public void Update(Func<Pairing, Pairing> change)
    {
        var p = Load();
        if (p is not null) Save(change(p));
    }

    public void Delete() { if (File.Exists(Paths.Token)) File.Delete(Paths.Token); }
}
