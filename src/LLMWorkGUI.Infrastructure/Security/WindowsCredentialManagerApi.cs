using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LLMWorkGUI.Infrastructure.Security;

/// <summary>
/// The production <see cref="ICredentialManagerApi"/>: per-user generic credentials in the current
/// user's Credential Manager, persisted with <c>CRED_PERSIST_LOCAL_MACHINE</c> so the value survives
/// a logoff and is still scoped to the one Windows profile the application supports (ADR-0005 §1.1).
///
/// Every entry point is classified instead of being allowed to throw: a native failure is a state
/// the store has to reason about, not an exception that could bypass the DPAPI fallback decision.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManagerApi : ICredentialManagerApi
{
    private const uint GenericCredentialType = 1; // CRED_TYPE_GENERIC
    private const uint PersistLocalMachine = 2; // CRED_PERSIST_LOCAL_MACHINE

    // Serialize the native credential boundary across instances. Concurrent calls on
    // this Windows runtime can report successful writes followed by stale/missing reads
    // even for independent targets. Never hold a lease across an application await.
    private static readonly object NativeCallGate = new();

    public CredentialManagerReadResult Read(string target)
    {
        lock (NativeCallGate)
        {
            return ReadCore(target);
        }
    }

    private static CredentialManagerReadResult ReadCore(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!OperatingSystem.IsWindows())
        {
            return CredentialManagerReadResult.Failure(CredentialManagerOutcome.ApiUnavailable);
        }

        IntPtr credentialPointer = IntPtr.Zero;

        try
        {
            if (!NativeMethods.CredRead(target, GenericCredentialType, 0, out credentialPointer))
            {
                return CredentialManagerReadResult.Failure(Classify(Marshal.GetLastWin32Error()));
            }

            // The block is owned by advapi32 and is released in the finally block, so it is copied
            // once into a buffer this application can clear.
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            return CredentialManagerReadResult.Success(CopyBlob(credential));
        }
        catch (Exception exception) when (IsApiUnavailable(exception))
        {
            return CredentialManagerReadResult.Failure(CredentialManagerOutcome.ApiUnavailable);
        }
        finally
        {
            if (credentialPointer != IntPtr.Zero)
            {
                NativeMethods.CredFree(credentialPointer);
            }
        }
    }

    public CredentialManagerOperationResult Write(string target, string secret, string userName, string comment)
    {
        lock (NativeCallGate)
        {
            return WriteCore(target, secret, userName, comment);
        }
    }

    private static CredentialManagerOperationResult WriteCore(string target, string secret, string userName, string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(comment);

        if (!OperatingSystem.IsWindows())
        {
            return CredentialManagerOperationResult.Failure(CredentialManagerOutcome.ApiUnavailable);
        }

        // No terminating null: the value is exactly the UTF-8 bytes of the secret, which is what makes
        // an empty or null-terminated blob detectable as a corrupt record on the way back.
        var blob = Encoding.UTF8.GetBytes(secret);
        IntPtr blobPointer = IntPtr.Zero;
        IntPtr targetPointer = IntPtr.Zero;
        IntPtr userNamePointer = IntPtr.Zero;
        IntPtr commentPointer = IntPtr.Zero;

        try
        {
            blobPointer = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            targetPointer = Marshal.StringToHGlobalUni(target);
            userNamePointer = Marshal.StringToHGlobalUni(userName);
            commentPointer = Marshal.StringToHGlobalUni(comment);

            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                Comment = commentPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = PersistLocalMachine,
                UserName = userNamePointer
            };

            return NativeMethods.CredWrite(ref credential, 0)
                ? CredentialManagerOperationResult.Success()
                : CredentialManagerOperationResult.Failure(Classify(Marshal.GetLastWin32Error()));
        }
        catch (Exception exception) when (IsApiUnavailable(exception))
        {
            return CredentialManagerOperationResult.Failure(CredentialManagerOutcome.ApiUnavailable);
        }
        finally
        {
            if (blobPointer != IntPtr.Zero)
            {
                // The unmanaged copy of a value that has just been handed to the credential manager
                // is scrubbed; the persisted copy is Windows' responsibility.
                Marshal.Copy(new byte[blob.Length], 0, blobPointer, blob.Length);
                Marshal.FreeHGlobal(blobPointer);
            }

            FreeStringPointer(targetPointer);
            FreeStringPointer(userNamePointer);
            FreeStringPointer(commentPointer);
            Array.Clear(blob);
        }
    }

    public CredentialManagerOperationResult Delete(string target)
    {
        lock (NativeCallGate)
        {
            return DeleteCore(target);
        }
    }

    private static CredentialManagerOperationResult DeleteCore(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!OperatingSystem.IsWindows())
        {
            return CredentialManagerOperationResult.Failure(CredentialManagerOutcome.ApiUnavailable);
        }

        try
        {
            return NativeMethods.CredDelete(target, GenericCredentialType, 0)
                ? CredentialManagerOperationResult.Success()
                : CredentialManagerOperationResult.Failure(Classify(Marshal.GetLastWin32Error()));
        }
        catch (Exception exception) when (IsApiUnavailable(exception))
        {
            return CredentialManagerOperationResult.Failure(CredentialManagerOutcome.ApiUnavailable);
        }
    }

    private static byte[] CopyBlob(NativeCredential credential)
    {
        if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        var length = checked((int)credential.CredentialBlobSize);
        var blob = new byte[length];
        Marshal.Copy(credential.CredentialBlob, blob, 0, length);
        return blob;
    }

    private static void FreeStringPointer(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool IsApiUnavailable(Exception exception) =>
        exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;

    private static CredentialManagerOutcome Classify(int error) => error switch
    {
        1168 => CredentialManagerOutcome.NotFound,
        50 => CredentialManagerOutcome.NotSupported,
        5 => CredentialManagerOutcome.AccessDenied,
        1312 => CredentialManagerOutcome.NoLogonSession,
        87 => CredentialManagerOutcome.InvalidParameter,
        _ => CredentialManagerOutcome.Failed
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;

        // FILETIME is two DWORDs, i.e. eight bytes in total. Laying it out as anything wider shifts
        // every following field and makes CredWrite fail with ERROR_INVALID_PARAMETER.
        public uint LastWrittenLow;
        public uint LastWrittenHigh;

        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(
            string target,
            uint type,
            int reserved,
            out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        internal static extern void CredFree(IntPtr buffer);
    }
}
