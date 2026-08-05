using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

public sealed class HybridCLRReferenceUtilityTests
{
	[Test]
	public void generatedAotListCanBeReadWithoutAssemblyReference()
	{
		IReadOnlyList<string> actual = HybridCLRReferenceUtility.getPatchedAOTAssemblyList();
		Assert.That(actual, Is.Not.Null);
		Assert.That(actual, Is.All.Not.Null.And.Not.Empty);
		Assert.That(actual.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(actual.Count));

		Type generated = AppDomain.CurrentDomain.GetAssemblies()
			.Select(assembly => assembly.GetType("AOTGenericReferences", false))
			.FirstOrDefault(type => type != null);
		if (generated == null)
		{
			Assert.That(actual, Is.Empty);
			return;
		}

		FieldInfo field = generated.GetField("PatchedAOTAssemblyList",
			BindingFlags.Public | BindingFlags.Static);
		Assert.That(field, Is.Not.Null);
		IEnumerable<string> expected = field.GetValue(null) as IEnumerable<string>;
		CollectionAssert.AreEqual(expected, actual);
	}
}
