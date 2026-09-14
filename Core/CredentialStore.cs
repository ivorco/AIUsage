using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace AIUsage.Core;

/// <summary>
/// Settings live in Windows Credential Manager (generic credentials, encrypted by Windows for the
/// current user) — never in files. Values larger than one credential blob are split across numbered targets.
/// </summary>
public static class CredentialStore
{
    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;
    const int ErrorNotFound = 1168;
    const int ChunkBytes = 2048; // CRED_MAX_CREDENTIAL_BLOB_SIZE is 2560

    static string Target(string providerId, string key, int chunk) =>
        chunk == 0 ? $"AIUsage/{providerId}/{key}" : $"AIUsage/{providerId}/{key}/part{chunk}";

    public static bool Exists(string providerId, string key) => ReadChunk(Target(providerId, key, 0)) is not null;

    public static string? Read(string providerId, string key)
    {
        if (ReadChunk(Target(providerId, key, 0)) is not { } first)
            return null;

        using var buffer = new MemoryStream();
        buffer.Write(first);
        for (int i = 1; ReadChunk(Target(providerId, key, i)) is { } part; i++)
            buffer.Write(part);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static void Write(string providerId, string key, string value)
    {
        Delete(providerId, key);
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            int chunk = 0;
            for (int offset = 0; offset < bytes.Length || chunk == 0; offset += ChunkBytes, chunk++)
                WriteChunk(Target(providerId, key, chunk), bytes, offset, Math.Min(ChunkBytes, bytes.Length - offset));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public static void Delete(string providerId, string key)
    {
        for (int i = 0; CredDelete(Target(providerId, key, i), CredTypeGeneric, 0); i++)
        {
        }
    }

    static byte[]? ReadChunk(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            int error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            var data = new byte[credential.CredentialBlobSize];
            if (data.Length > 0)
                Marshal.Copy(credential.CredentialBlob, data, 0, data.Length);
            return data;
        }
        finally
        {
            CredFree(handle);
        }
    }

    static void WriteChunk(string target, byte[] bytes, int offset, int length)
    {
        int size = Math.Max(1, length);
        var blob = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, offset, blob, length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = length,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[size], 0, blob, size);
            Marshal.FreeHGlobal(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
