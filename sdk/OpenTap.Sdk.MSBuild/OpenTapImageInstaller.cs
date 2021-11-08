﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Build.Framework;
using OpenTap;
using OpenTap.Diagnostic;
using OpenTap.Package;

namespace Keysight.OpenTap.Sdk.MSBuild
{
    internal class OpenTapImageInstaller : IDisposable
    {
        public string TapDir { get; set; }
        public CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// This object represents a trace listener of the type EventTraceListener from the <see cref="OpenTAP"/> assembly
        /// </summary>
        private EventTraceListener traceListener;
        void onEvent(IEnumerable<Event> events)
        {
            var logLevelMap = new Dictionary<int, string>()
            {
                [10] = "Error",
                [20] = "Warning",
                [30] = "Information",
                [40] = "Debug",
            };

            var mutedSources = new HashSet<string>()
            {
                "Searcher", "PluginManager", "TypeDataProvider", "Resolver", "Serializer"
            };

            foreach (var evt in events)
            {
                if (mutedSources.Contains(evt.Source)) continue;

                var msg = $"{evt.Source} : {evt.Message}";
                switch (logLevelMap[evt.EventType])
                {
                    case "Error":
                        OnError?.Invoke(msg);
                        break;
                    case "Warning":
                        OnWarning?.Invoke(msg);
                        break;
                    case "Information":
                        OnInfo?.Invoke(msg);
                        break;
                    case "Debug":
                        OnDebug?.Invoke(msg);
                        break;
                }
            }
        }

        /// <summary>
        /// Instantiate an OpenTAP trace listener and create a delegate to handle log messages
        /// </summary>
        /// <exception cref="Exception"></exception>
        void attachTraceListener()
        {
            traceListener = new EventTraceListener();
            traceListener.MessageLogged += onEvent;
            Log.AddListener(traceListener);
        }

        public OpenTapImageInstaller(string tapDir, CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            TapDir = tapDir;
            var targetInstall = new Installation(TapDir);
            InstalledPackages = targetInstall.GetPackages()
                .Where(p => p.Class.Equals("system-wide", StringComparison.OrdinalIgnoreCase) == false).ToArray();
            attachTraceListener();
        }

        /// <summary>
        /// Creates a merged image of currently installed packages and the packages contained in ITaskItem[]
        /// and deploys it to the output directory.
        /// </summary>
        /// <param name="packagesToInstall"></param>
        /// <returns></returns>
        public bool InstallImage(ITaskItem[] packagesToInstall)
        {
            bool success = true;
            var imageString = CreateJsonImageSpecifier(packagesToInstall);
            OnDebug?.Invoke($"Trying to deploy '{imageString.Replace('\n', ' ')}'");

            bool isCompatible(IPackageIdentifier actual, PackageSpecifier specifier)
            {
                // Returns true if 'actual' is compatible with 'specifier'.
                return specifier.Name == actual.Name && specifier.Version.IsCompatible(actual.Version) &&
                       actual.IsPlatformCompatible(specifier.Architecture, specifier.OS);
            }

            try
            {
                var imageSpecifier = ImageSpecifier.FromString(imageString);
                var installed = InstalledPackages;

                imageSpecifier.OnResolve += args =>
                    installed.FirstOrDefault(i => isCompatible(i, args.PackageSpecifier));

                var imageIdentifier = imageSpecifier.Resolve(CancellationToken);
                imageIdentifier.Deploy(TapDir, CancellationToken);
            }
            catch (AggregateException aex)
            {
                OnError?.Invoke(aex.Message);
                foreach (var ex in aex.InnerExceptions)
                {
                    OnError?.Invoke(ex.Message);
                }

                success = false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
                success = false;
            }
            finally
            {
                if (success == false)
                {
                    OnError?.Invoke($"Failed to resolve image.");
                    OnDebug?.Invoke($"{string.Join('\n', imageString)}");
                }
            }

            return success;
        }


        public Action<string> OnDebug;
        public Action<string> OnInfo;
        public Action<string> OnWarning;
        public Action<string> OnError;

        public void Dispose()
        {
            Log.RemoveListener(traceListener);
        }

        private PackageDef[] InstalledPackages;

        /// <summary>
        /// Convert the <see cref="ITaskItem[]"/> to a JSON image specifier
        /// </summary>
        /// <param name="taskItems"></param>
        /// <returns></returns>
        private string CreateJsonImageSpecifier(ITaskItem[] taskItems)
        {
            // OpenTAP should not be installed or updated through these package elements.
            // The version should only be managed through a NuGet <PackageReference/> tag.
            taskItems = taskItems.Where(t => t.ItemSpec != "OpenTAP").ToArray();
            var repositories = taskItems.Select(i => i.GetMetadata("Repository"))
                                        .Where(r => string.IsNullOrWhiteSpace(r) == false).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // packages.opentap.io is required for images to resolve in some cases. There's no harm in adding it.
            if (repositories.Any(r => r.IndexOf("packages.opentap.io", StringComparison.OrdinalIgnoreCase) > 0) == false)
                repositories.Add("packages.opentap.io");

            PackageSpecifier fromTaskItem(ITaskItem i)
            {
                var name = i.ItemSpec;
                var versionString = i.GetMetadata("Version");
                var archString = i.GetMetadata("Architecture");
                var os = i.GetMetadata("OS");

                if (Enum.TryParse(archString, out CpuArchitecture arch) == false)
                    arch = CpuArchitecture.Unspecified;

                if (VersionSpecifier.TryParse(versionString, out var version) == false)
                    version = VersionSpecifier.Any;

                return new PackageSpecifier(name, version, arch, os);
            }

            var items = taskItems.Select(fromTaskItem).ToList();

            // Currently installed packages should be merged with the requested packages
            var installed = InstalledPackages;
            foreach (var pkg in installed)
            {
                // .csproj elements should always take precedence over installed packages.
                if (items.Any(i => i.Name == pkg.Name) == false)
                    items.Add(new PackageSpecifier(pkg.Name, VersionSpecifier.Parse(pkg.Version.ToString()),
                        pkg.Architecture, pkg.OS));
            }

            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions() { Indented = true, SkipValidation = false }))
            {
                w.WriteStartObject();

                { // Write Packages element
                    w.WriteStartArray("Packages");
                    foreach (var item in items)
                    {
                        w.WriteStartObject();
                        w.WriteString("Name", item.Name);
                        w.WriteString(nameof(item.Version), item.Version.ToString());
                        if (string.IsNullOrWhiteSpace(item.OS) == false)
                            w.WriteString(nameof(item.OS), item.OS);
                        w.WriteString(nameof(item.Architecture), item.Architecture.ToString());
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }
                { // Write Repositories element
                    w.WriteStartArray("Repositories");
                    foreach (var repo in repositories)
                    {
                        w.WriteStringValue(repo);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }

            // Utf8JsonWriter doesn't write to the memory stream before it has been disposed.
            // This means we cannot use the scoped using language feature for the writer, and we cannot return from inside
            // the Using scope.
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}