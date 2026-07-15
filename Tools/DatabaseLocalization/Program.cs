using System.Security.Cryptography;
using System.Text.Json;
using Server;
using Server.MirEnvir;

namespace DatabaseLocalization;

internal static class Program
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int Main(string[] args)
    {
        try
        {
            CommandLine command = CommandLine.Parse(args);
            return command.Command switch
            {
                "export" => Export(command),
                "validate" => Validate(command),
                "apply" => Apply(command),
                "report" => Report(command),
                _ => ShowUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Export(CommandLine command)
    {
        string databasePath = RequireDatabase(command);
        string outputPath = command.GetPath("output", Path.ChangeExtension(databasePath, ".zh-CN.json"));
        DatabaseContext context = DatabaseContext.Load(databasePath);

        TranslationDocument document = TranslationDocument.Create(context);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(document, JsonOptions));

        Console.WriteLine($"Exported {document.Entries.Count} entries to:");
        Console.WriteLine(outputPath);
        PrintEntryCounts(document.Entries);
        Console.WriteLine("Edit only the translation values. Preserve key, kind, index, field, and source.");
        return 0;
    }

    private static int Validate(CommandLine command)
    {
        string databasePath = RequireDatabase(command);
        string inputPath = RequirePath(command, "input");
        TranslationDocument document = ReadDocument(inputPath);
        DatabaseContext context = DatabaseContext.Load(databasePath);
        ValidationResult result = ValidateDocument(document, context, command.HasFlag("allow-unsafe-names"));

        PrintValidation(result);
        return result.Errors.Count == 0 ? 0 : 2;
    }

    private static int Report(CommandLine command)
    {
        string inputPath = RequirePath(command, "input");
        TranslationDocument document = ReadDocument(inputPath);
        List<TranslationEntry> translated = document.Entries
            .Where(entry => entry.HasTranslation)
            .ToList();

        Console.WriteLine($"Translation file: {Path.GetFullPath(inputPath)}");
        Console.WriteLine($"Entries: {document.Entries.Count}");
        Console.WriteLine($"Translated: {translated.Count}");
        Console.WriteLine($"Remaining: {document.Entries.Count - translated.Count}");
        Console.WriteLine($"Unsafe name changes: {translated.Count(entry => entry.IsUnsafeNameChange)}");
        PrintEntryCounts(translated);
        return 0;
    }

    private static int Apply(CommandLine command)
    {
        string databasePath = RequireDatabase(command);
        string inputPath = RequirePath(command, "input");
        TranslationDocument document = ReadDocument(inputPath);
        DatabaseContext context = DatabaseContext.Load(databasePath);
        bool allowUnsafeNames = command.HasFlag("allow-unsafe-names");
        ValidationResult result = ValidateDocument(document, context, allowUnsafeNames);

        PrintValidation(result);
        if (result.Errors.Count > 0)
        {
            Console.Error.WriteLine("No database changes were made.");
            return 2;
        }

        List<TranslationEntry> changes = document.Entries
            .Where(entry => entry.HasTranslation)
            .ToList();

        if (changes.Count == 0)
        {
            Console.WriteLine("No translations to apply.");
            return 0;
        }

        Console.WriteLine($"Validated {changes.Count} change(s).");
        if (!command.HasFlag("write"))
        {
            Console.WriteLine("Dry run complete. Add --write to create a backup and update Server.MirDB.");
            return 0;
        }

        string backupPath = CreateBackup(databasePath);
        foreach (TranslationEntry entry in changes)
        {
            ApplyEntry(context.Edit, entry);
        }

        context.Edit.SaveDB();
        Console.WriteLine($"Applied {changes.Count} change(s).");
        Console.WriteLine($"Backup: {backupPath}");
        Console.WriteLine("The server must be restarted before it reads the updated database.");
        return 0;
    }

    private static TranslationDocument ReadDocument(string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Translation file not found.", inputPath);
        }

        TranslationDocument? document = JsonSerializer.Deserialize<TranslationDocument>(File.ReadAllText(inputPath), JsonOptions);
        if (document is null)
        {
            throw new InvalidOperationException("Translation file is empty or invalid JSON.");
        }

        return document;
    }

    private static ValidationResult ValidateDocument(TranslationDocument document, DatabaseContext context, bool allowUnsafeNames)
    {
        ValidationResult result = new();
        if (document.SchemaVersion != SchemaVersion)
        {
            result.Errors.Add($"Unsupported schema version {document.SchemaVersion}. Expected {SchemaVersion}.");
        }

        if (!string.Equals(document.DatabaseSha256, context.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add("The database has changed since this translation file was exported. Export again, then merge translations.");
        }

        foreach (TranslationEntry entry in document.Entries)
        {
            ValidateEntry(entry, context.Edit, allowUnsafeNames, result);
        }

        return result;
    }

    private static void ValidateEntry(TranslationEntry entry, Envir database, bool allowUnsafeNames, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Kind) || string.IsNullOrWhiteSpace(entry.Field))
        {
            result.Errors.Add("An entry is missing key, kind, or field.");
            return;
        }

        string? current = ReadEntryValue(database, entry);
        if (current is null)
        {
            result.Errors.Add($"{entry.Key}: target no longer exists or field is invalid.");
            return;
        }

        if (!string.Equals(current, entry.Source, StringComparison.Ordinal))
        {
            result.Errors.Add($"{entry.Key}: source text no longer matches the database.");
        }

        if (!entry.HasTranslation)
        {
            return;
        }

        if (entry.Translation!.Contains('\0'))
        {
            result.Errors.Add($"{entry.Key}: translation contains a null character.");
        }

        if (entry.IsUnsafeNameChange && !allowUnsafeNames)
        {
            result.Errors.Add($"{entry.Key}: item and monster names are reference keys. Use --allow-unsafe-names only after updating all scripts and drop files.");
        }

        if (entry.IsUnsafeNameChange && allowUnsafeNames)
        {
            result.Warnings.Add($"{entry.Key}: changing this name may break drop tables, quest files, scripts, and hard-coded monster settings.");
        }
    }

    private static string? ReadEntryValue(Envir database, TranslationEntry entry)
    {
        return (entry.Kind, entry.Field) switch
        {
            ("item", "name") => database.ItemInfoList.FirstOrDefault(item => item.Index == entry.Index)?.Name,
            ("item", "toolTip") => database.ItemInfoList.FirstOrDefault(item => item.Index == entry.Index)?.ToolTip ?? string.Empty,
            ("monster", "name") => database.MonsterInfoList.FirstOrDefault(monster => monster.Index == entry.Index)?.Name,
            ("npc", "name") => database.NPCInfoList.FirstOrDefault(npc => npc.Index == entry.Index)?.Name,
            ("map", "title") => database.MapInfoList.FirstOrDefault(map => map.Index == entry.Index)?.Title,
            ("magic", "name") => database.MagicInfoList.FirstOrDefault(magic => (byte)magic.Spell == entry.Index)?.Name,
            ("quest", "name") => database.QuestInfoList.FirstOrDefault(quest => quest.Index == entry.Index)?.Name,
            ("quest", "gotoMessage") => database.QuestInfoList.FirstOrDefault(quest => quest.Index == entry.Index)?.GotoMessage,
            ("quest", "killMessage") => database.QuestInfoList.FirstOrDefault(quest => quest.Index == entry.Index)?.KillMessage,
            ("quest", "itemMessage") => database.QuestInfoList.FirstOrDefault(quest => quest.Index == entry.Index)?.ItemMessage,
            ("quest", "flagMessage") => database.QuestInfoList.FirstOrDefault(quest => quest.Index == entry.Index)?.FlagMessage,
            _ => null,
        };
    }

    private static void ApplyEntry(Envir database, TranslationEntry entry)
    {
        string value = entry.Translation!;
        switch (entry.Kind, entry.Field)
        {
            case ("item", "name"):
                database.ItemInfoList.Single(item => item.Index == entry.Index).Name = value;
                break;
            case ("item", "toolTip"):
                database.ItemInfoList.Single(item => item.Index == entry.Index).ToolTip = value;
                break;
            case ("monster", "name"):
                database.MonsterInfoList.Single(monster => monster.Index == entry.Index).Name = value;
                break;
            case ("npc", "name"):
                database.NPCInfoList.Single(npc => npc.Index == entry.Index).Name = value;
                break;
            case ("map", "title"):
                database.MapInfoList.Single(map => map.Index == entry.Index).Title = value;
                break;
            case ("magic", "name"):
                database.MagicInfoList.Single(magic => (byte)magic.Spell == entry.Index).Name = value;
                break;
            case ("quest", "name"):
                database.QuestInfoList.Single(quest => quest.Index == entry.Index).Name = value;
                break;
            case ("quest", "gotoMessage"):
                database.QuestInfoList.Single(quest => quest.Index == entry.Index).GotoMessage = value;
                break;
            case ("quest", "killMessage"):
                database.QuestInfoList.Single(quest => quest.Index == entry.Index).KillMessage = value;
                break;
            case ("quest", "itemMessage"):
                database.QuestInfoList.Single(quest => quest.Index == entry.Index).ItemMessage = value;
                break;
            case ("quest", "flagMessage"):
                database.QuestInfoList.Single(quest => quest.Index == entry.Index).FlagMessage = value;
                break;
            default:
                throw new InvalidOperationException($"Unsupported entry {entry.Key}.");
        }
    }

    private static void PrintValidation(ValidationResult result)
    {
        foreach (string warning in result.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }

        foreach (string error in result.Errors)
        {
            Console.Error.WriteLine($"Validation error: {error}");
        }

        if (result.Errors.Count == 0)
        {
            Console.WriteLine("Validation passed.");
        }
    }

    private static void PrintEntryCounts(IEnumerable<TranslationEntry> entries)
    {
        foreach (IGrouping<string, TranslationEntry> group in entries.GroupBy(entry => entry.Kind).OrderBy(group => group.Key))
        {
            Console.WriteLine($"{group.Key}: {group.Count()}");
        }
    }

    private static string CreateBackup(string databasePath)
    {
        string directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "LocalizationBackups");
        Directory.CreateDirectory(directory);
        string name = $"Server.MirDB.{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
        string backupPath = Path.Combine(directory, name);
        File.Copy(databasePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static string RequireDatabase(CommandLine command)
    {
        return RequirePath(command, "database");
    }

    private static string RequirePath(CommandLine command, string name)
    {
        string? path = command.GetValue(name);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"Missing required --{name} argument.");
        }

        return Path.GetFullPath(path);
    }

    private static int ShowUsage()
    {
        Console.WriteLine("DatabaseLocalization - Server.MirDB localization export/import tool");
        Console.WriteLine();
        Console.WriteLine("export   --database <Server.MirDB> [--output <translations.json>]");
        Console.WriteLine("report   --input <translations.json>");
        Console.WriteLine("validate --database <Server.MirDB> --input <translations.json> [--allow-unsafe-names]");
        Console.WriteLine("apply    --database <Server.MirDB> --input <translations.json> [--write] [--allow-unsafe-names]");
        Console.WriteLine();
        Console.WriteLine("apply is a dry run unless --write is supplied.");
        return 1;
    }
}

internal sealed class DatabaseContext
{
    public required Envir Edit { get; init; }
    public required string DatabasePath { get; init; }
    public required string Sha256 { get; init; }
    public required int Version { get; init; }

    public static DatabaseContext Load(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Server.MirDB not found.", databasePath);
        }

        if (!string.Equals(Path.GetFileName(databasePath), "Server.MirDB", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The database file must be named Server.MirDB. The server's binary reader uses that runtime path.");
        }

        string directory = Path.GetDirectoryName(databasePath)!;
        string originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(directory);

        try
        {
            Settings.Load();
            Envir edit = Envir.Edit;
            if (!edit.LoadDB())
            {
                throw new InvalidOperationException("The server rejected this database version.");
            }

            return new DatabaseContext
            {
                Edit = edit,
                DatabasePath = databasePath,
                Sha256 = ComputeSha256(databasePath),
                Version = Envir.LoadVersion,
            };
        }
        catch
        {
            Directory.SetCurrentDirectory(originalDirectory);
            throw;
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed class TranslationDocument
{
    public int SchemaVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string DatabaseFile { get; set; } = string.Empty;
    public string DatabaseSha256 { get; set; } = string.Empty;
    public int DatabaseVersion { get; set; }
    public List<TranslationEntry> Entries { get; set; } = [];

    public static TranslationDocument Create(DatabaseContext context)
    {
        TranslationDocument document = new()
        {
            SchemaVersion = 1,
            CreatedUtc = DateTime.UtcNow,
            DatabaseFile = Path.GetFileName(context.DatabasePath),
            DatabaseSha256 = context.Sha256,
            DatabaseVersion = context.Version,
        };

        Envir database = context.Edit;
        document.Entries.AddRange(database.ItemInfoList.OrderBy(item => item.Index).Select(item =>
            TranslationEntry.Create("item", item.Index, "name", item.Name, "unsafe", "Also used by scripts and item lookup.")));
        document.Entries.AddRange(database.ItemInfoList.OrderBy(item => item.Index)
            .Where(item => !string.IsNullOrWhiteSpace(item.ToolTip))
            .Select(item => TranslationEntry.Create("item", item.Index, "toolTip", item.ToolTip, "safe", "Player-visible item tooltip.")));
        document.Entries.AddRange(database.MonsterInfoList.OrderBy(monster => monster.Index).Select(monster =>
            TranslationEntry.Create("monster", monster.Index, "name", monster.Name, "unsafe", "Also used by drops, scripts, and some server settings.")));
        document.Entries.AddRange(database.NPCInfoList.OrderBy(npc => npc.Index).Select(npc =>
            TranslationEntry.Create("npc", npc.Index, "name", npc.Name, "safe", "NPC script file name is not changed.")));
        document.Entries.AddRange(database.MapInfoList.OrderBy(map => map.Index).Select(map =>
            TranslationEntry.Create("map", map.Index, "title", map.Title, "safe", "Map file name is not changed.")));
        document.Entries.AddRange(database.MagicInfoList.OrderBy(magic => (byte)magic.Spell).Select(magic =>
            TranslationEntry.Create("magic", (byte)magic.Spell, "name", magic.Name, "safe", "Spell enum is not changed.")));

        foreach (var quest in database.QuestInfoList.OrderBy(quest => quest.Index))
        {
            document.Entries.Add(TranslationEntry.Create("quest", quest.Index, "name", quest.Name, "safe", "Quest file name is not changed."));
            AddIfNotEmpty(document, quest.Index, "gotoMessage", quest.GotoMessage);
            AddIfNotEmpty(document, quest.Index, "killMessage", quest.KillMessage);
            AddIfNotEmpty(document, quest.Index, "itemMessage", quest.ItemMessage);
            AddIfNotEmpty(document, quest.Index, "flagMessage", quest.FlagMessage);
        }

        return document;
    }

    private static void AddIfNotEmpty(TranslationDocument document, int index, string field, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            document.Entries.Add(TranslationEntry.Create("quest", index, field, value, "safe", "Quest message stored in Server.MirDB."));
        }
    }
}

internal sealed class TranslationEntry
{
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Index { get; set; }
    public string Field { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Translation { get; set; }
    public string Safety { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation) && !string.Equals(Source, Translation, StringComparison.Ordinal);
    public bool IsUnsafeNameChange => HasTranslation && Safety == "unsafe";

    public static TranslationEntry Create(string kind, int index, string field, string source, string safety, string note)
    {
        return new TranslationEntry
        {
            Key = $"{kind}:{index}:{field}",
            Kind = kind,
            Index = index,
            Field = field,
            Source = source,
            Translation = string.Empty,
            Safety = safety,
            Note = note,
        };
    }
}

internal sealed class ValidationResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

internal sealed class CommandLine
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _flags;

    public string Command { get; }

    private CommandLine(string command, Dictionary<string, string> values, HashSet<string> flags)
    {
        Command = command;
        _values = values;
        _flags = flags;
    }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CommandLine(string.Empty, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {argument}");
            }

            string name = argument[2..];
            if (name is "write" or "allow-unsafe-names")
            {
                flags.Add(name);
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Argument --{name} needs a value.");
            }

            values[name] = args[++index];
        }

        return new CommandLine(args[0].ToLowerInvariant(), values, flags);
    }

    public string? GetValue(string name) => _values.GetValueOrDefault(name);
    public string GetPath(string name, string fallback) => Path.GetFullPath(GetValue(name) ?? fallback);
    public bool HasFlag(string name) => _flags.Contains(name);
}
