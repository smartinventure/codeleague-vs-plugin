using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace CodeLeague.PersistentChanges.Core
{
    [DataContract]
    public sealed class TrackedFile
    {
        /// <summary>ISO timestamp when the file was first observed as changed after the baseline.</summary>
        [DataMember(Name = "firstSeen", Order = 1)]
        public string FirstSeen { get; set; } = "";

        /// <summary>ISO timestamp of the latest observed modification.</summary>
        [DataMember(Name = "lastSeen", Order = 2)]
        public string LastSeen { get; set; } = "";

        /// <summary>Fingerprint ("size:mtimeMs" or "deleted") at the latest observation.</summary>
        [DataMember(Name = "fingerprint", Order = 3, EmitDefaultValue = false)]
        public string? Fingerprint { get; set; }
    }

    [DataContract]
    public sealed class Baseline
    {
        [DataMember(Name = "id", Order = 1)]
        public string Id { get; set; } = "";

        [DataMember(Name = "createdAt", Order = 2)]
        public string CreatedAt { get; set; } = "";

        /// <summary>Fingerprints of files that were already dirty when the baseline was created.</summary>
        [DataMember(Name = "snapshot", Order = 3)]
        public Dictionary<string, string> Snapshot { get; set; } = new Dictionary<string, string>();

        public static Baseline Create(Dictionary<string, string>? snapshot, DateTime nowUtc) => new Baseline
        {
            Id = Guid.NewGuid().ToString(),
            CreatedAt = StateStore.ToIso(nowUtc),
            Snapshot = snapshot ?? new Dictionary<string, string>(),
        };
    }

    [DataContract]
    public sealed class BranchState
    {
        [DataMember(Name = "baseline", Order = 1)]
        public Baseline Baseline { get; set; } = new Baseline();

        /// <summary>Tracked files keyed by repository-relative path (forward slashes).</summary>
        [DataMember(Name = "files", Order = 2)]
        public Dictionary<string, TrackedFile> Files { get; set; } = new Dictionary<string, TrackedFile>();

        /// <summary>HEAD commit at the last refresh on this branch.</summary>
        [DataMember(Name = "lastHead", Order = 3, EmitDefaultValue = false)]
        public string? LastHead { get; set; }
    }

    [DataContract]
    public sealed class PersistedState
    {
        [DataMember(Name = "version", Order = 1)]
        public int Version { get; set; } = StateStore.StateVersion;

        [DataMember(Name = "repositoryRoot", Order = 2)]
        public string RepositoryRoot { get; set; } = "";

        [DataMember(Name = "branches", Order = 3)]
        public Dictionary<string, BranchState> Branches { get; set; } = new Dictionary<string, BranchState>();
    }

    /// <summary>
    /// Persistent tracking state for one repository work tree. The file format and location are shared with the
    /// VS Code extension (vscode/src/core/stateStore.ts), so both IDEs show the same list.
    /// </summary>
    public sealed class StateStore
    {
        public const int StateVersion = 1;

        private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(
            typeof(PersistedState),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        private readonly object _gate = new object();
        private readonly string _repositoryRoot;
        private string _loadedVersion = "";

        public StateStore(string filePath, string repositoryRoot)
        {
            FilePath = filePath;
            _repositoryRoot = repositoryRoot;
            State = Empty();
            Load();
        }

        public string FilePath { get; }

        public PersistedState State { get; private set; }

        public static StateStore ForRepository(string root, string? storageDirectory = null) =>
            new StateStore(Path.Combine(storageDirectory ?? DefaultStorageDirectory(), StateFileName(root)), root);

        public static string DefaultStorageDirectory() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeLeague", "PersistentChanges");

        /// <summary>Forward slashes, no trailing slash, lower-case on Windows – identical to normalizeRoot() in TypeScript.</summary>
        public static string NormalizeRoot(string root, bool windows = true)
        {
            var n = root.Replace('\\', '/').TrimEnd('/');
            return windows ? n.ToLowerInvariant() : n;
        }

        /// <summary>"&lt;folder&gt;-&lt;sha1(normalizedRoot)[0..16]&gt;.json" – identical to stateFileName() in TypeScript.</summary>
        public static string StateFileName(string root, bool windows = true)
        {
            var normalized = NormalizeRoot(root, windows);
            string hash;
            using (var sha1 = SHA1.Create())
            {
                hash = string.Concat(sha1.ComputeHash(Encoding.UTF8.GetBytes(normalized)).Select(b => b.ToString("x2"))).Substring(0, 16);
            }
            var name = normalized.Split('/').LastOrDefault(s => s.Length > 0) ?? "repo";
            name = new string(name.Select(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' ? c : '_').ToArray());
            return $"{name}-{hash}.json";
        }

        public static string ToIso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// <summary>Re-reads the file if another process (e.g. the other IDE) changed it.</summary>
        public bool ReloadIfChanged()
        {
            lock (_gate)
            {
                if (CurrentVersion() == _loadedVersion)
                {
                    return false;
                }
                Load();
                return true;
            }
        }

        /// <summary>Reads the state under the store lock (the state may be mutated concurrently by a background refresh).</summary>
        public T Read<T>(Func<PersistedState, T> reader)
        {
            lock (_gate)
            {
                return reader(State);
            }
        }

        /// <summary>Read-modify-write. The mutator returns true if it changed the state.</summary>
        public bool Update(Func<PersistedState, bool> mutator)
        {
            lock (_gate)
            {
                ReloadIfChanged();
                if (!mutator(State))
                {
                    return false;
                }
                Write();
                return true;
            }
        }

        private void Load()
        {
            var version = CurrentVersion();
            PersistedState? state = null;
            if (version.Length > 0)
            {
                try
                {
                    using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        state = Serializer.ReadObject(stream) as PersistedState;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is SerializationException || ex is UnauthorizedAccessException)
                {
                    // Corrupt or partially written file: start over rather than failing.
                }
            }
            State = Normalize(state) ?? Empty();
            _loadedVersion = version;
        }

        private void Write()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = $"{FilePath}.{System.Diagnostics.Process.GetCurrentProcess().Id}.{DateTime.UtcNow.Ticks}.tmp";
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                Serializer.WriteObject(stream, State);
            }
            if (File.Exists(FilePath))
            {
                File.Replace(tmp, FilePath, null);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
            _loadedVersion = CurrentVersion();
        }

        /// <summary>Cheap change detector for the state file: "" when it does not exist.</summary>
        private string CurrentVersion()
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";
        }

        private PersistedState Empty() => new PersistedState { RepositoryRoot = _repositoryRoot };

        /// <summary>DataContract deserialization skips constructors/initializers, so missing members are null.</summary>
        private static PersistedState? Normalize(PersistedState? state)
        {
            if (state == null)
            {
                return null;
            }
            state.Branches ??= new Dictionary<string, BranchState>();
            foreach (var b in state.Branches.Values.Where(b => b != null))
            {
                b.Files ??= new Dictionary<string, TrackedFile>();
                b.Baseline ??= new Baseline();
                b.Baseline.Snapshot ??= new Dictionary<string, string>();
            }
            foreach (var key in state.Branches.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
            {
                state.Branches.Remove(key);
            }
            return state;
        }
    }
}
