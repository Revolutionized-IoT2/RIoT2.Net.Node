using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Node.Tests;

/// <summary>
/// Verified plugin updates (design 7.2 phase 4, I35): a staged package restarts the node with its configuration
/// saved, a refused one leaves the plugins alone and reports <c>failed</c>, and a package that doesn't load is
/// rolled back once, the way Program starts.
/// </summary>
[TestClass]
[DoNotParallelize]
public class PluginUpdateTests
{
    private const string PackageUrl = "http://plugins.test/RIoT2.Net.Devices_9.9.9.zip";

    [TestMethod]
    public async Task StagedPackageSavesTheConfigurationAndRestartsTheNode()
    {
        await using var plc = new SimulatedEasyPlc();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staged = new PackageManifest { Name = "Devices plugin", Version = "9.9.9", InstalledPackageFilename = "RIoT2.Net.Devices_9.9.9.zip" };
        await using var node = await NodeIntegrationHarness.StartAsync(
            updatePlugins: (desired, _) => Task.FromResult(PluginUpdateResult.Staged(staged)),
            restart: () => restarted.TrySetResult());

        var configuration = node.ConfigurationFor(plc);
        configuration.PluginPackageUrl = PackageUrl;
        var desired = await node.SendDesiredAsync(configuration, 7);

        await node.StatusAsync(s => s.State == NodeConfigurationState.PendingRestart);
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsEmpty(node.Reconfigurations, "No device starts before the restart.");
        var saved = Json.Deserialize<NodeDeviceConfiguration>(File.ReadAllText(Path.Combine(node.DataFolder, "configuration.applied.json")))!;
        Assert.AreEqual(desired.Hash, saved.Hash, "The node restarts with the configuration that names the new package.");
        Assert.AreEqual(PackageUrl, saved.PluginPackageUrl);
    }

    [TestMethod]
    public async Task RefusedPackageKeepsThePluginsAppliesTheDevicesAndReportsFailed()
    {
        await using var plc = new SimulatedEasyPlc();
        var checks = 0;
        await using var node = await NodeIntegrationHarness.StartAsync(
            updatePlugins: (desired, _) =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(PluginUpdateResult.Rejected("It was built against RIoT2.Core 2.0.0."));
            },
            restart: () => Assert.Fail("A refused package must not restart the node."));

        var configuration = node.ConfigurationFor(plc);
        configuration.PluginPackageUrl = PackageUrl;
        var desired = await node.SendDesiredAsync(configuration, 1);

        var failed = await node.StatusAsync(s => s.State == NodeConfigurationState.Failed);
        Assert.AreEqual("It was built against RIoT2.Core 2.0.0.", failed.Error);
        Assert.AreEqual(RIoT2.Core.DeviceState.Running, node.Device.State, "The devices run with the installed plugins.");

        // The same command again isn't "applied", so the node checks again (a 304 and the cached refusal), and restarts nothing.
        node.DrainStatuses();
        await node.ResendAsync(desired);
        await node.StatusAsync(s => s.State == NodeConfigurationState.Failed);
        Assert.AreEqual(2, Volatile.Read(ref checks));
        Assert.AreEqual(1, node.Reconfigurations.Sum(r => r.Started), "The device started once.");
    }

    [TestMethod]
    public async Task UnavailablePackageAppliesTheConfiguration()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync(
            updatePlugins: (desired, _) => Task.FromResult(PluginUpdateResult.Unavailable("Could not fetch the sidecar.")));

        var configuration = node.ConfigurationFor(plc);
        configuration.PluginPackageUrl = PackageUrl;
        var desired = await node.SendDesiredAsync(configuration, 1);

        Assert.AreEqual(desired.Hash, (await node.StatusAsync(s => s.State == NodeConfigurationState.Applied)).AppliedHash);
    }

    // The released Devices plugin zip and its sidecar (packed by the test build), installed the way Program starts:
    // stage, promote, load, confirm. Then a package whose DLL doesn't load is rolled back once.
    [TestMethod]
    public async Task PackedPluginInstallsAndABrokenUpdateIsRolledBackOnce()
    {
        var packed = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "PackedPlugin"), "RIoT2.Net.Devices_*.zip").Single();
        var root = Path.Combine(Path.GetTempPath(), "riot2-plugin-update-" + Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(plugins);
        try
        {
            var installer = new PluginPackageInstaller(plugins, Path.Combine(root, "Data"));
            var files = new Dictionary<string, byte[]>
            {
                [PackageUrl] = File.ReadAllBytes(packed),
                [PackageUrl + ".sha256"] = File.ReadAllBytes(packed + ".sha256"),
            };
            Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(files.TryGetValue(request.RequestUri!.AbsoluteUri, out var content)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

            // A node without plugins installs the configured package.
            var first = await installer.UpdateAsync(PackageUrl, null, false, Send);
            Assert.AreEqual(PluginUpdateOutcome.Staged, first.Outcome, first.Message);
            Assert.AreEqual(PluginPackageInstaller.ComputeSha256(packed), first.Package!.Sha256);
            Assert.IsNotNull(installer.PromoteStaged());
            Assert.IsTrue(File.Exists(Path.Combine(plugins, "RIoT2.Net.Devices.dll")));
            var installed = Json.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(plugins, "PluginManifest.json")))!;
            Assert.AreEqual(first.Package.Sha256, installed.Sha256);
            Assert.AreEqual(PluginStartupOutcome.Installed, installer.ConfirmStartup(true).Outcome);

            // An update whose DLL isn't an assembly.
            const string brokenUrl = "http://plugins.test/RIoT2.Net.Devices_10.0.0.zip";
            var broken = Zip(("RIoT2.Net.Devices.dll", "not an assembly"), ("PluginManifest.json",
                $"{{ \"name\": \"Devices plugin\", \"version\": \"10.0.0\", \"coreVersion\": \"{PluginCompatibility.HostCoreVersion.ToString(3)}\" }}"));
            files[brokenUrl] = broken;
            files[brokenUrl + ".sha256"] = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(broken)) + "  RIoT2.Net.Devices_10.0.0.zip\n");
            Assert.AreEqual(PluginUpdateOutcome.Staged, (await installer.UpdateAsync(brokenUrl, installed, true, Send)).Outcome);

            Assert.IsNotNull(installer.PromoteStaged());
            var load = PluginLoader.Load(plugins, new ServiceCollection(), _ => { }, NullLogger.Instance);
            Assert.IsFalse(load.Succeeded);
            StringAssert.Contains(load.Error, "RIoT2.Net.Devices.dll");
            var startup = installer.ConfirmStartup(load.Succeeded, load.Error);
            Assert.AreEqual(PluginStartupOutcome.RolledBack, startup.Outcome);
            Assert.AreEqual(first.Package.Sha256, Json.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(plugins, "PluginManifest.json")))!.Sha256);

            // The restart after the rollback loads the previous package and confirms nothing: no second restart.
            Assert.IsNull(installer.PromoteStaged());
            var services = new ServiceCollection();
            var reload = PluginLoader.Load(plugins, services, _ => { }, NullLogger.Instance);
            Assert.IsTrue(reload.Succeeded, reload.Error);
            Assert.AreEqual(1, reload.Loaded);
            Assert.IsTrue(services.Any(s => s.ServiceType == typeof(RIoT2.Core.Interfaces.IDevice)));
            Assert.AreEqual(PluginStartupOutcome.None, installer.ConfirmStartup(reload.Succeeded, reload.Error).Outcome);

            // The orchestrator still names the broken package: it is refused without a download.
            var again = await installer.UpdateAsync(brokenUrl, installed, true, Send);
            Assert.AreEqual(PluginUpdateOutcome.Rejected, again.Outcome);
            StringAssert.Contains(again.Message, "didn't load");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } // Windows: the load contexts keep the DLLs open
        }
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        return stream.ToArray();
    }
}
