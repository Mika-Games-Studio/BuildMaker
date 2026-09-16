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
