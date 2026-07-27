param(
    [Parameter(Mandatory = $true)]
    [string]$ChineseLibrary,

    [Parameter(Mandatory = $true)]
    [string]$CurrentLibrary
)

$ErrorActionPreference = 'Stop'

function Read-Library([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $reader = [IO.BinaryReader]::new($stream)

    try {
        $version = $reader.ReadInt32()
        $count = $reader.ReadInt32()
        if ($version -lt 3) {
            throw "Unsupported library version $version in $Path"
        }

        $frameOffset = $reader.ReadInt32()
        $offsets = [int[]]::new($count)
        for ($index = 0; $index -lt $count; $index++) {
            $offsets[$index] = $reader.ReadInt32()
        }

        $entries = [object[]]::new($count)
        for ($index = 0; $index -lt $count; $index++) {
            $start = $offsets[$index]
            $end = if ($index + 1 -lt $count) { $offsets[$index + 1] } else { $frameOffset }
            if ($start -lt 0 -or $end -lt $start -or $end -gt $bytes.Length) {
                throw "Invalid entry range at index $index in $Path"
            }

            $stream.Position = $start
            $width = $reader.ReadInt16()
            $height = $reader.ReadInt16()
            $x = $reader.ReadInt16()
            $y = $reader.ReadInt16()
            $shadowX = $reader.ReadInt16()
            $shadowY = $reader.ReadInt16()
            $shadow = $reader.ReadByte()
            $dataLength = $reader.ReadInt32()
            $stream.Position += $dataLength

            $maskSignature = ''
            if (($shadow -band 0x80) -ne 0) {
                $maskWidth = $reader.ReadInt16()
                $maskHeight = $reader.ReadInt16()
                $maskX = $reader.ReadInt16()
                $maskY = $reader.ReadInt16()
                $maskLength = $reader.ReadInt32()
                $maskSignature = "$maskWidth,$maskHeight,$maskX,$maskY"
                $stream.Position += $maskLength
            }

            if ($stream.Position -ne $end) {
                throw "Entry length mismatch at index $index in $Path"
            }

            $entryBytes = [byte[]]::new($end - $start)
            [Array]::Copy($bytes, $start, $entryBytes, 0, $entryBytes.Length)
            $entries[$index] = [pscustomobject]@{
                Bytes = $entryBytes
                Layout = "$width,$height,$x,$y,$shadowX,$shadowY,$shadow,$maskSignature"
            }
        }

        $frameBytes = [byte[]]::new($bytes.Length - $frameOffset)
        [Array]::Copy($bytes, $frameOffset, $frameBytes, 0, $frameBytes.Length)

        return [pscustomobject]@{
            Version = $version
            Count = $count
            Entries = $entries
            FrameBytes = $frameBytes
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Write-MergedLibrary($Chinese, $Current, [string]$Path) {
    $output = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($output)

    try {
        $headerLength = 12 + ($Current.Count * 4)
        $selectedEntries = [object[]]::new($Current.Count)
        $replaced = [Collections.Generic.List[int]]::new()

        for ($index = 0; $index -lt $Current.Count; $index++) {
            $currentEntry = $Current.Entries[$index]
            if ($index -lt $Chinese.Count -and
                $Chinese.Entries[$index].Layout -eq $currentEntry.Layout -and
                -not [Linq.Enumerable]::SequenceEqual[byte]($Chinese.Entries[$index].Bytes, $currentEntry.Bytes)) {
                $selectedEntries[$index] = $Chinese.Entries[$index]
                $replaced.Add($index)
            }
            else {
                $selectedEntries[$index] = $currentEntry
            }
        }

        $frameOffset = $headerLength
        foreach ($entry in $selectedEntries) {
            $frameOffset += $entry.Bytes.Length
        }

        $writer.Write($Current.Version)
        $writer.Write($Current.Count)
        $writer.Write($frameOffset)

        $offset = $headerLength
        foreach ($entry in $selectedEntries) {
            $writer.Write($offset)
            $offset += $entry.Bytes.Length
        }

        foreach ($entry in $selectedEntries) {
            $writer.Write($entry.Bytes)
        }
        $writer.Write($Current.FrameBytes)
        $writer.Flush()

        [IO.File]::WriteAllBytes($Path, $output.ToArray())
        return $replaced
    }
    finally {
        $writer.Dispose()
        $output.Dispose()
    }
}

$sourcePath = [IO.Path]::GetFullPath($ChineseLibrary)
$targetPath = [IO.Path]::GetFullPath($CurrentLibrary)
if (-not [IO.File]::Exists($sourcePath)) { throw "Chinese library not found: $sourcePath" }
if (-not [IO.File]::Exists($targetPath)) { throw "Current library not found: $targetPath" }

$chinese = Read-Library $sourcePath
$current = Read-Library $targetPath
$temporaryPath = "$targetPath.merge.tmp"
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = "$targetPath.before-chinese-$timestamp.bak"

try {
    $replaced = Write-MergedLibrary $chinese $current $temporaryPath
    $validation = Read-Library $temporaryPath
    if ($validation.Version -ne $current.Version -or $validation.Count -ne $current.Count) {
        throw 'Merged library validation failed.'
    }

    $commonCount = [Math]::Min($chinese.Count, $current.Count)
    $compatibleCount = 0
    $identicalCount = 0
    for ($index = 0; $index -lt $commonCount; $index++) {
        if ($chinese.Entries[$index].Layout -eq $current.Entries[$index].Layout) {
            $compatibleCount++
            if ([Linq.Enumerable]::SequenceEqual[byte]($chinese.Entries[$index].Bytes, $current.Entries[$index].Bytes)) {
                $identicalCount++
            }
        }
    }

    [IO.File]::Replace($temporaryPath, $targetPath, $backupPath)

    $result = [pscustomobject]@{
        ChineseCount = $chinese.Count
        CurrentCount = $current.Count
        ReplacedCount = $replaced.Count
        AlreadyIdenticalCount = $identicalCount
        IncompatibleLayoutCount = $commonCount - $compatibleCount
        PreservedNewCount = [Math]::Max(0, $current.Count - $chinese.Count)
        BackupPath = $backupPath
        ReplacedIndices = ($replaced -join ',')
    }
    $result | ConvertTo-Json -Depth 3
}
finally {
    if ([IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
}
