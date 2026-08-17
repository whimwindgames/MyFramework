using System;
using System.IO;
using MyFramework.BuildStudio.Editor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MyFramework.BuildStudio.Tests
{
	public sealed class MfBuildStudioTests
	{
		[Test]
		public void StructureGenerationIsDeterministic()
		{
			MfProjectStructure first = MfProjectStructureService.generate();
			MfProjectStructure second = MfProjectStructureService.generate();

			Assert.That(first.structureHash, Is.EqualTo(second.structureHash));
			Assert.That(MfProjectStructureService.serialize(first, Formatting.None),
				Is.EqualTo(MfProjectStructureService.serialize(second, Formatting.None)));
		}

		[Test]
		public void StrictReaderRejectsUnknownFields()
		{
			string path = temporary("unknown.json");
			try
			{
				JObject json = JObject.Parse(MfProjectStructureService.serialize(
					MfProjectStructureService.generate(), Formatting.None));
				json["unexpected"] = true;
				File.WriteAllText(path, json.ToString(Formatting.None));

				Assert.Throws<InvalidDataException>(() =>
					MfProjectStructureService.read(path, false));
			}
			finally { if (File.Exists(path)) File.Delete(path); }
		}

		[Test]
		public void StructureRoundTripPreservesVerifiedHash()
		{
			string path = temporary("roundtrip.json");
			try
			{
				MfProjectStructure generated = MfProjectStructureService.generate(path);
				MfProjectStructureService.writeAtomic(path,
					MfProjectStructureService.serialize(generated, Formatting.Indented));

				MfProjectStructure loaded = MfProjectStructureService.read(path, true);
				Assert.That(loaded.structureHash, Is.EqualTo(generated.structureHash));
			}
			finally { if (File.Exists(path)) File.Delete(path); }
		}

		[Test]
		public void ValidateJobProducesReceiptAndEvents()
		{
			MfProjectStructure structure = MfProjectStructureService.generate();
			MfProjectStructureService.writeDefault();
			string jobPath = temporary("job.json");
			string receiptPath = temporary("receipt.json");
			string eventPath = temporary("events.jsonl");
			try
			{
				MfBuildJob job = new()
				{
					jobId = "test-validate-job",
					projectRoot = MfProjectStructureService.projectRoot(),
					structureHash = structure.structureHash,
					profileId = "validate",
					action = "validate",
					target = "Current",
					environment = "test",
				};
				File.WriteAllText(jobPath, MfProjectStructureService.serialize(job,
					Formatting.Indented));

				int code = MfBuildCli.run(jobPath, receiptPath, eventPath,
					out MfBuildReceipt receipt);

				Assert.That(code, Is.Zero);
				Assert.That(receipt.ok, Is.True);
				Assert.That(receipt.stages.Count, Is.EqualTo(1));
				Assert.That(File.Exists(receiptPath), Is.True);
				Assert.That(File.ReadAllLines(eventPath).Length, Is.EqualTo(2));
			}
			finally
			{
				foreach (string path in new[] { jobPath, receiptPath, eventPath })
					if (File.Exists(path)) File.Delete(path);
			}
		}

		static string temporary(string name)
		{
			string root = Path.Combine(MfProjectStructureService.projectRoot(), "Temp",
				"BuildStudioTests");
			Directory.CreateDirectory(root);
			return Path.Combine(root, "mf-build-studio-" +
				Guid.NewGuid().ToString("N") + "-" + name);
		}
	}
}
