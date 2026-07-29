using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Reflection;

namespace ExternalSnapshot.ContractTests
{
    [TestClass]
    public sealed class ExternalSnapshotOverrideNormalizationTests
    {
        [TestMethod]
        public void NormalizeProviderOverride_PreservesExternalSnapshotPresenceOnlyOverride()
        {
            var repositoryRoot = FindRepositoryRoot();
            var assemblyPath = Path.Combine(
                repositoryRoot,
                "source",
                "bin",
                "Release",
                "PlayniteAchievements.dll");

            Assert.IsTrue(File.Exists(assemblyPath), $"Plugin assembly not found: {assemblyPath}");

            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
            ResolveEventHandler resolver = (_, args) =>
            {
                var dependencyName = new AssemblyName(args.Name).Name + ".dll";
                var dependencyPath = Path.Combine(assemblyDirectory, dependencyName);
                return File.Exists(dependencyPath) ? Assembly.LoadFrom(dependencyPath) : null;
            };

            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try
            {
                var assembly = Assembly.LoadFrom(assemblyPath);
                var overrideType = assembly.GetType(
                    "PlayniteAchievements.Models.Settings.ProviderOverrideData",
                    throwOnError: true);
                var normalizerType = assembly.GetType(
                    "PlayniteAchievements.Services.GameCustomData.GameCustomDataNormalizer",
                    throwOnError: true);

                var providerOverride = Activator.CreateInstance(overrideType);
                overrideType.GetProperty("ProviderKey")?.SetValue(providerOverride, "ExternalSnapshot");
                overrideType.GetProperty("Value")?.SetValue(providerOverride, null);

                var normalizeMethod = normalizerType.GetMethod(
                    "NormalizeProviderOverride",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert.IsNotNull(normalizeMethod, "NormalizeProviderOverride was not found.");

                var normalized = normalizeMethod.Invoke(null, new[] { providerOverride });
                Assert.IsNotNull(normalized, "ExternalSnapshot override was discarded by normalization.");
                Assert.AreEqual(
                    "ExternalSnapshot",
                    overrideType.GetProperty("ProviderKey")?.GetValue(normalized) as string);
                Assert.IsNull(overrideType.GetProperty("Value")?.GetValue(normalized));
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "source", "PlayniteAchievements.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Repository root could not be located from the test output directory.");
        }
    }
}
