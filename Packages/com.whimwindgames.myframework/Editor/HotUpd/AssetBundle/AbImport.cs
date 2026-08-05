using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using UObject = UnityEngine.Object;

// 只维护显式AB计划缓存和SpriteAtlas成员边界；不持久改写项目资源导入配置。
public sealed class AbImport : AssetPostprocessor
{
	public static void OnPostprocessAllAssets(string[] importedAssets,
		string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
	{
		if ((importedAssets?.Length ?? 0) != 0 ||
			(deletedAssets?.Length ?? 0) != 0 ||
			(movedAssets?.Length ?? 0) != 0 ||
			(movedFromAssetPaths?.Length ?? 0) != 0)
		{
			AbCfg.invalidate();
		}
	}

	// 校验图集及其成员边界，不修改Importer。
	public static void norm()
	{
		if (AbPipe.isRun || BuildPipeline.isBuildingPlayer)
		{
			throw new InvalidOperationException("构建期间不能检查图集配置");
		}
		AbPlan plan = AbPlan.make(AbCfg.load());
		AbCheck.need(plan);
		string[] files = atlasFiles(plan);
		Debug.Log("图集配置检查完成，图集:" + files.Length + "；未修改资源导入设置");
	}

	internal static SpriteAtlasImporter atlasIn(string file)
	{
		return AssetImporter.GetAtPath(file) as SpriteAtlasImporter ??
			throw new InvalidDataException("图集输入加载失败:" + file);
	}

	internal static HashSet<string> atlasTex(AbPlan scope, string[] files)
	{
		if (scope == null) throw new ArgumentNullException(nameof(scope));
		Dictionary<string, string> atlases = new(StringComparer.Ordinal);
		foreach (string file in files ?? Array.Empty<string>())
		{
			SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(file) ??
				throw new InvalidDataException("图集加载失败:" + file);
			HashSet<string> own = new(StringComparer.Ordinal);
			foreach (UObject item in atlas.GetPackables())
			{
				addTex(scope, atlases, own, file, AssetDatabase.GetAssetPath(item));
			}
		}
		return new HashSet<string>(atlases.Keys, StringComparer.Ordinal);
	}

	static void addTex(AbPlan scope, Dictionary<string, string> atlases,
		HashSet<string> own, string atlas, string path)
	{
		if (AssetDatabase.IsValidFolder(path))
		{
			string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { path });
			Array.Sort(guids, StringComparer.Ordinal);
			foreach (string guid in guids)
			{
				string asset = AssetDatabase.GUIDToAssetPath(guid);
				if (AssetImporter.GetAtPath(asset) is not TextureImporter importer ||
					importer.textureType != TextureImporterType.Sprite) continue;
				if (!scope.contains(asset)) throw new InvalidDataException(
					"图集Packable未进入AB计划:" + atlas + " -> " + asset);
				addOne(atlases, own, atlas, asset);
			}
			return;
		}

		if (!scope.contains(path)) throw new InvalidDataException(
			"图集Packable越界:" + atlas + " -> " +
			(string.IsNullOrEmpty(path) ? "<empty>" : path));
		if (AssetImporter.GetAtPath(path) is TextureImporter texture &&
			texture.textureType == TextureImporterType.Sprite)
		{
			addOne(atlases, own, atlas, path);
		}
	}

	static void addOne(Dictionary<string, string> atlases, HashSet<string> own,
		string atlas, string path)
	{
		if (!own.Add(path)) return;
		if (atlases.TryGetValue(path, out string old)) throw new InvalidDataException(
			"图集成员重复:" + path + " -> " + old + " / " + atlas);
		atlases.Add(path, atlas);
	}

	internal static string[] atlasFiles(AbPlan plan)
	{
		if (plan == null) throw new ArgumentNullException(nameof(plan));
		List<string> values = new();
		foreach (AbPkg pkg in plan.pkgs)
		foreach (AbAst ast in pkg.asts)
		{
			if (AssetDatabase.LoadAssetAtPath<SpriteAtlas>(ast.path) != null)
				values.Add(ast.path);
		}
		values.Sort(StringComparer.Ordinal);
		return values.ToArray();
	}
}
