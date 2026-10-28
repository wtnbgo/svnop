namespace SvnOp;

/// <summary>1Password ⇔ wincrypt キャッシュの同期と削除。CLI (sync / lock / add) と agent (起動時・トレイ) で共用</summary>
public static class CacheSync
{
    public sealed record SyncResult(int Written, int UpToDate, int Adopted, int Skipped)
    {
        public string Summary(bool dryRun) => L.T(
            $"{(dryRun ? "書き込み予定" : "書き込み")} {Written} 件 / 最新 {UpToDate} 件 / 管理対象に追加 {Adopted} 件 / スキップ {Skipped} 件",
            $"{(dryRun ? "would write" : "written")} {Written} / up to date {UpToDate} / now managed {Adopted} / skipped {Skipped}");
    }

    public sealed record LockResult(int Removed, int Kept)
    {
        public string Summary => L.T($"削除 {Removed} 件 / 残し {Kept} 件", $"deleted {Removed} / kept {Kept}");
    }

    /// <summary>
    /// items を wincrypt キャッシュに書き出す。
    /// svnop が書いた後に変更されたファイルや、svnop 管理外で内容の違うファイルは force でなければ触らない。
    /// </summary>
    public static SyncResult Sync(IEnumerable<SvnItem> items, bool dryRun, bool force, Action<string> report)
    {
        var managed = ManagedList.Load();
        var current = AuthCache.ReadAll(withPassword: true).ToDictionary(e => e.File, StringComparer.OrdinalIgnoreCase);
        int written = 0, adopted = 0, skipped = 0, upToDate = 0;
        foreach (var item in items.OrderBy(i => i.Realm))
        {
            if (string.IsNullOrEmpty(item.Password))
                continue;
            var file = AuthCache.FileFor(item.Realm);
            var label = $"{item.Realm} ({item.Username})";
            var same = current.TryGetValue(file, out var cur) && cur.Username == item.Username && cur.Password == item.Password;
            if (same && managed.Files.ContainsKey(file))
            {
                upToDate++;
                continue;
            }
            if (managed.Files.TryGetValue(file, out var m))
            {
                if (File.Exists(file) && !ManagedList.Unchanged(file, m) && !force)
                {
                    report(L.T("スキップ (svnop が書いた後に変更されています。--force で上書き): ",
                               "skipped (changed after svnop wrote it; --force to overwrite): ") + label);
                    skipped++;
                    continue;
                }
            }
            else if (cur != null)
            {
                if (same)
                {
                    // 同じ内容が既にある: 書き換えずに管理下に置く
                    if (!dryRun)
                        managed.Files[file] = new ManagedList.Entry(item.Realm, item.Username, AuthCache.HashOf(File.ReadAllBytes(file)));
                    report(L.T("同じ内容のキャッシュあり (管理対象に追加): ", "identical entry exists (now managed): ") + label);
                    adopted++;
                    continue;
                }
                if (!force)
                {
                    report(L.T("スキップ (内容の違う既存キャッシュあり。--force で上書き): ",
                               "skipped (an existing entry differs; --force to overwrite): ") + label);
                    skipped++;
                    continue;
                }
            }
            if (dryRun)
            {
                report(L.T("書き込み予定: ", "would write: ") + label);
                written++;
                continue;
            }
            var (path, hash) = AuthCache.WriteWincrypt(item.Realm, item.Username, item.Password);
            managed.Files[path] = new ManagedList.Entry(item.Realm, item.Username, hash);
            report(L.T("書き込み: ", "written: ") + label);
            written++;
        }
        if (!dryRun)
            managed.Save();
        return new SyncResult(written, upToDate, adopted, skipped);
    }

    /// <summary>svnop が書いた (管理下の) キャッシュを消す。書いた後に変更されたものは force でなければ残す</summary>
    public static LockResult Lock(bool force, Action<string> report)
    {
        var managed = ManagedList.Load();
        int removed = 0, kept = 0;
        foreach (var (file, e) in managed.Files.ToList())
        {
            if (!File.Exists(file))
            {
                managed.Files.Remove(file);
                continue;
            }
            if (!ManagedList.Unchanged(file, e) && !force)
            {
                report(L.T("残しました (svnop が書いた後に変更されています。--force で削除): ",
                           "kept (changed after svnop wrote it; --force to delete): ") + $"{e.Realm} ({e.User})");
                kept++;
                continue;
            }
            File.Delete(file);
            managed.Files.Remove(file);
            removed++;
        }
        managed.Save();
        return new LockResult(removed, kept);
    }
}
