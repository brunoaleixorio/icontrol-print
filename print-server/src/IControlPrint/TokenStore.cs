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
        Restrict(Paths.Token);
    }

    public void Update(Func<Pairing, Pairing> change)
    {
        var p = Load();
        if (p is not null) Save(change(p));
    }

    // DPAPI LocalMachine: qualquer usuário da máquina decifraria; por isso o arquivo fica só para SYSTEM e Administradores
    static void Restrict(string path)
    {
        try
        {
            var sec = new System.Security.AccessControl.FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(sec);
        }
        catch { /* sem permissão para mudar ACL: segue com a herança da pasta */ }
    }

    public void Delete() { if (File.Exists(Paths.Token)) File.Delete(Paths.Token); }
}
