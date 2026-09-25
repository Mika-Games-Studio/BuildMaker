using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UnityLocalCI.Core.Secrets;

/// <summary>
/// Windows Credential Manager via CredRead. P/Invoke direto para nao
/// acrescentar dependencia externa por causa de duas funcoes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int ERROR_NOT_FOUND = 1168;

    public bool Exists(string credentialName) => Read(credentialName) is not null;

    public string? Read(string credentialName)
    {
        if (string.IsNullOrWhiteSpace(credentialName)) return null;

        if (!CredRead(credentialName, CRED_TYPE_GENERIC, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return null;
            throw new InvalidOperationException(
                $"Falha ao ler a credencial '{credentialName}' do Windows Credential Manager (erro {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return string.Empty;

            // O blob e gravado como UTF-16 por cmdkey e pelo Credential Manager.
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <summary>
    /// Grava com persistencia LOCAL_MACHINE: a credencial sobrevive ao logoff,
    /// mas continua sendo da conta do Windows que gravou. O cofre e por usuario
    /// — uma conta de servico precisa da sua propria copia.
    /// </summary>
    public void Write(string credentialName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialName);
        ArgumentNullException.ThrowIfNull(secret);

        var blob = Marshal.StringToCoTaskMemUni(secret);
        var alvo = Marshal.StringToCoTaskMemUni(credentialName);
        var usuario = Marshal.StringToCoTaskMemUni("pat");

        try
        {
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = alvo,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(secret.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = usuario,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"Falha ao gravar a credencial '{credentialName}' no Windows Credential Manager " +
                    $"(erro {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            // Zera o segredo antes de devolver a memoria: um bloco liberado com
            // o token ainda escrito pode acabar num despejo de memoria.
            for (var i = 0; i < secret.Length; i++) Marshal.WriteInt16(blob, i * 2, 0);

            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(alvo);
            Marshal.FreeCoTaskMem(usuario);
        }
    }

    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
