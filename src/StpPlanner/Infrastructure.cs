using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using IHorizonStorage = Maptek.Vulcan.Stp.Engine.IHorizonStorage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>The bridge calls the planner needs (seam for tests); <see cref="StpBridgeGateway"/> is the real one.</summary>
    public interface IPlannerBridge
    {
        /// <summary><c>GET /snapshot</c>.</summary>
        /// <param name="current">The current snapshot (same project check).</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The refreshed snapshot.</returns>
        Task<SnapshotV2> GetSnapshotAsync(SnapshotV2 current, CancellationToken cancellationToken);

        /// <summary><c>POST /apply-package</c>; the receipt is verified against the package.</summary>
        /// <param name="package">The package.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The receipt.</returns>
        Task<ApplyPackageSaved> SubmitApplyPackageAsync(ApplyPackage package, CancellationToken cancellationToken);
    }

    /// <summary><see cref="IPlannerBridge"/> over <see cref="StpBridgeClient"/>.</summary>
    public sealed class StpBridgeGateway : IPlannerBridge, IDisposable
    {
        private readonly StpBridgeClient client;

        /// <summary>Initializes a new instance of the <see cref="StpBridgeGateway"/> class.</summary>
        /// <param name="client">The client (owned).</param>
        public StpBridgeGateway(StpBridgeClient client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <inheritdoc/>
        public Task<SnapshotV2> GetSnapshotAsync(SnapshotV2 current, CancellationToken cancellationToken) => client.GetSnapshotAsync(current, cancellationToken);

        /// <inheritdoc/>
        public Task<ApplyPackageSaved> SubmitApplyPackageAsync(ApplyPackage package, CancellationToken cancellationToken) => client.SubmitApplyPackageAsync(package, cancellationToken);

        /// <inheritdoc/>
        public void Dispose() => client.Dispose();
    }

    /// <summary>Horizon preferences kept in memory (tests, or when no folder is writable).</summary>
    public sealed class MemoryHorizonStorage : IHorizonStorage
    {
        private readonly Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Gets the stored items.</summary>
        public IReadOnlyDictionary<string, string> Items => data;

        /// <inheritdoc/>
        public string GetItem(string key) => data.TryGetValue(key, out string value) ? value : null;

        /// <inheritdoc/>
        public void SetItem(string key, string value) => data[key] = value;
    }

    /// <summary>
    /// Horizon preferences in one JSON object file (the plugin's stand-in for the browser's localStorage). A missing or
    /// unreadable file reads as empty; writes replace the file through a temporary sibling.
    /// </summary>
    public sealed class FileHorizonStorage : IHorizonStorage
    {
        private readonly string path;

        /// <summary>Initializes a new instance of the <see cref="FileHorizonStorage"/> class.</summary>
        /// <param name="path">The JSON file.</param>
        public FileHorizonStorage(string path)
        {
            this.path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <inheritdoc/>
        public string GetItem(string key) => Load().TryGetValue(key, out JToken value) && value.Type == JTokenType.String ? (string)value : null;

        /// <inheritdoc/>
        public void SetItem(string key, string value)
        {
            var data = Load();
            data[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".tmp";
            File.WriteAllText(temp, data.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private JObject Load()
        {
            try
            {
                return File.Exists(path) && JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) is JObject data ? data : new JObject();
            }
            catch (Exception error) when (error is IOException || error is JsonException || error is UnauthorizedAccessException)
            {
                return new JObject();
            }
        }
    }

    /// <summary>INotifyPropertyChanged base without WPF types.</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        /// <inheritdoc/>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
        /// <param name="name">The property.</param>
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Sets a field and raises the change when it differs.</summary>
        /// <typeparam name="T">Field type.</typeparam>
        /// <param name="field">The field.</param>
        /// <param name="value">The value.</param>
        /// <param name="name">The property.</param>
        /// <returns>Whether it changed.</returns>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
