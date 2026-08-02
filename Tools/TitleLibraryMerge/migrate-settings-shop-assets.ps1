param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDataDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ChineseDataDirectory
)

$ErrorActionPreference = 'Stop'

function Read-Library([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $version = $reader.ReadInt32()
        $count = $reader.ReadInt32()
        if ($version -lt 3) { throw "Unsupported library version $version in $Path" }

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
            if ($dataLength -lt 0 -or $stream.Position + $dataLength -gt $end) {
                throw "Invalid image payload at index $index in $Path"
            }
            $stream.Position += $dataLength

            $maskSignature = ''
            if (($shadow -band 0x80) -ne 0) {
                $maskWidth = $reader.ReadInt16()
                $maskHeight = $reader.ReadInt16()
                $maskX = $reader.ReadInt16()
                $maskY = $reader.ReadInt16()
                $maskLength = $reader.ReadInt32()
                if ($maskLength -lt 0 -or $stream.Position + $maskLength -gt $end) {
                    throw "Invalid mask payload at index $index in $Path"
                }
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
                Width = $width
                Height = $height
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

function Write-Library($Library, [object[]]$Entries, [string]$Path) {
    $headerLength = 12 + ($Entries.Count * 4)
    $frameOffset = $headerLength
    foreach ($entry in $Entries) { $frameOffset += $entry.Bytes.Length }

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($Library.Version)
        $writer.Write($Entries.Count)
        $writer.Write($frameOffset)

        $offset = $headerLength
        foreach ($entry in $Entries) {
            $writer.Write($offset)
            $offset += $entry.Bytes.Length
        }
        foreach ($entry in $Entries) { $writer.Write($entry.Bytes) }
        $writer.Write($Library.FrameBytes)
        $writer.Flush()
        [IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Test-BytesEqual([byte[]]$Left, [byte[]]$Right) {
    if ($null -eq $Left -or $null -eq $Right) { return $Left -eq $Right }
    if ($Left.Length -ne $Right.Length) { return $false }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) { return $false }
    }
    return $true
}

function Expand-Entry([byte[]]$Entry) {
    $stream = [IO.MemoryStream]::new($Entry, $false)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $header = $reader.ReadBytes(13)
        if ($header.Length -ne 13) { throw 'Truncated image header.' }
        $dataLength = $reader.ReadInt32()
        if ($dataLength -lt 0 -or $stream.Position + $dataLength -gt $stream.Length) {
            throw 'Invalid image payload length.'
        }
        $compressed = $reader.ReadBytes($dataLength)
        $trailing = $reader.ReadBytes([int]($stream.Length - $stream.Position))

        $input = [IO.MemoryStream]::new($compressed, $false)
        $gzip = [IO.Compression.GZipStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
        $output = [IO.MemoryStream]::new()
        try {
            $gzip.CopyTo($output)
            $pixels = $output.ToArray()
        }
        finally {
            $output.Dispose()
            $gzip.Dispose()
            $input.Dispose()
        }

        return [pscustomobject]@{
            Header = $header
            Width = [BitConverter]::ToInt16($header, 0)
            Height = [BitConverter]::ToInt16($header, 2)
            Pixels = $pixels
            Trailing = $trailing
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Clear-Text([byte[]]$Pixels, [int]$Width, [int]$Height) {
    # Preserve the 2-pixel border and the button's background gradient.
    $left = 4
    $right = $Width - 5
    $top = 3
    $bottom = $Height - 4
    if ($Width -ne 36 -or $Height -ne 17) {
        throw "Expected a 36x17 button, found ${Width}x${Height}."
    }

    for ($y = $top; $y -le $bottom; $y++) {
        $leftSample = (($y * $Width) + ($left - 1)) * 4
        $rightSample = (($y * $Width) + ($right + 1)) * 4
        for ($x = $left; $x -le $right; $x++) {
            $ratio = ($x - $left + 1.0) / ($right - $left + 2.0)
            $pixel = (($y * $Width) + $x) * 4
            for ($channel = 0; $channel -lt 4; $channel++) {
                $value = [Math]::Round($Pixels[$leftSample + $channel] * (1.0 - $ratio) + $Pixels[$rightSample + $channel] * $ratio)
                $Pixels[$pixel + $channel] = [byte]$value
            }
        }
    }
}

function Compress-Entry($Image) {
    $compressedStream = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($compressedStream, [IO.Compression.CompressionLevel]::Optimal, $true)
    try { $gzip.Write($Image.Pixels, 0, $Image.Pixels.Length) }
    finally { $gzip.Dispose() }
    $compressed = $compressedStream.ToArray()
    $compressedStream.Dispose()

    $entryStream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($entryStream)
    try {
        $writer.Write($Image.Header)
        $writer.Write($compressed.Length)
        $writer.Write($compressed)
        $writer.Write($Image.Trailing)
        $writer.Flush()
        return [pscustomobject]@{
            Bytes = $entryStream.ToArray()
            Width = $Image.Width
            Height = $Image.Height
            Layout = ''
        }
    }
    finally {
        $writer.Dispose()
        $entryStream.Dispose()
    }
}

function New-TextlessEntry($Entry) {
    $image = Expand-Entry $Entry.Bytes
    Clear-Text $image.Pixels $image.Width $image.Height
    return Compress-Entry $image
}

function Assert-EntrySet([object[]]$Entries, [object[]]$Expected, [int]$StartIndex, [string]$LibraryName) {
    if ($StartIndex + $Expected.Count -gt $Entries.Count) {
        throw "$LibraryName does not contain the expected appended entries starting at $StartIndex"
    }

    for ($offset = 0; $offset -lt $Expected.Count; $offset++) {
        if (-not (Test-BytesEqual $Entries[$StartIndex + $offset].Bytes $Expected[$offset].Bytes)) {
            throw "$LibraryName appended entry $($StartIndex + $offset) does not match the expected textless asset"
        }
    }
}

function Get-LibraryPath([string]$Directory, [string]$Name) {
    $path = [IO.Path]::GetFullPath((Join-Path $Directory "$Name.Lib"))
    if (-not [IO.File]::Exists($path)) { throw "Library not found: $path" }
    return $path
}

function Replace-Entries([string]$LibraryName, $Target, $Source, [int[]]$Indices) {
    foreach ($index in $Indices) {
        if ($index -ge $Target.Count -or $index -ge $Source.Count) {
            throw "$LibraryName index $index is outside target/source library bounds"
        }
        $Target.Entries[$index] = $Source.Entries[$index]
    }
}

function Publish-Library($Library, [string]$Path, [string]$BackupLabel) {
    $temporaryPath = "$Path.settings-shop.tmp"
    $backupPath = "$Path.before-$BackupLabel-$(Get-Date -Format 'yyyyMMdd-HHmmssfff').bak"
    try {
        Write-Library $Library $Library.Entries $temporaryPath
        $validation = Read-Library $temporaryPath
        if ($validation.Version -ne $Library.Version -or $validation.Count -ne $Library.Count) {
            throw "Validation failed for $temporaryPath"
        }
        [IO.File]::Replace($temporaryPath, $Path, $backupPath)
        return $backupPath
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) { [IO.File]::Delete($temporaryPath) }
    }
}

$targetTitlePath = Get-LibraryPath $TargetDataDirectory 'Title'
$sourceTitlePath = Get-LibraryPath $ChineseDataDirectory 'Title'
$targetPrguse2Path = Get-LibraryPath $TargetDataDirectory 'Prguse2'

$targetTitle = Read-Library $targetTitlePath
$sourceTitle = Read-Library $sourceTitlePath
$targetPrguse2 = Read-Library $targetPrguse2Path
$originalTitleCount = $targetTitle.Count
$originalPrguse2Count = $targetPrguse2.Count

if ($originalTitleCount -ne 899 -and $originalTitleCount -ne 904) {
    throw "Unexpected Title library count $originalTitleCount; expected 899 before migration or 904 after migration"
}
if ($originalPrguse2Count -ne 1602 -and $originalPrguse2Count -ne 1620) {
    throw "Unexpected Prguse2 library count $originalPrguse2Count; expected 1602 before migration or 1620 after migration"
}

if ($targetTitle.Version -ne $sourceTitle.Version) {
    throw "Version mismatch for Title: target $($targetTitle.Version), source $($sourceTitle.Version)"
}

# These two entries are complete Chinese raster assets from the supplied pack.
$titleCopiedChanged = $false
foreach ($index in @(26, 411)) {
    if (-not (Test-BytesEqual $targetTitle.Entries[$index].Bytes $sourceTitle.Entries[$index].Bytes)) {
        $titleCopiedChanged = $true
    }
}
Replace-Entries 'Title' $targetTitle $sourceTitle @(26, 411)

# Append private textless states. The client uses these indices so shared chat
# buttons at Prguse2[462..467] and movement artwork remain unchanged.
$prguse2Textless = [Collections.Generic.List[object]]::new()
foreach ($index in 450..467) {
    $prguse2Textless.Add((New-TextlessEntry $targetPrguse2.Entries[$index]))
}
$titleTextless = [Collections.Generic.List[object]]::new()
foreach ($index in @(848, 850, 851, 852, 853)) {
    $titleTextless.Add((New-TextlessEntry $targetTitle.Entries[$index]))
}

$titleAppended = $false
if ($originalTitleCount -eq 899) {
    foreach ($entry in $titleTextless) { $targetTitle.Entries += $entry }
    $titleAppended = $true
}
else {
    Assert-EntrySet $targetTitle.Entries $titleTextless 899 'Title'
}

$prguse2Appended = $false
if ($originalPrguse2Count -eq 1602) {
    foreach ($entry in $prguse2Textless) { $targetPrguse2.Entries += $entry }
    $prguse2Appended = $true
}
else {
    Assert-EntrySet $targetPrguse2.Entries $prguse2Textless 1602 'Prguse2'
}

$targetTitle.Count = $targetTitle.Entries.Count
$targetPrguse2.Count = $targetPrguse2.Entries.Count

$titleBackup = if ($titleCopiedChanged -or $titleAppended) {
    Publish-Library $targetTitle $targetTitlePath 'settings-shop-20260801'
}
else { '' }
$prguse2Backup = if ($prguse2Appended) {
    Publish-Library $targetPrguse2 $targetPrguse2Path 'settings-shop-20260801'
}
else { '' }

[pscustomobject]@{
    TitlePath = $targetTitlePath
    TitleBackup = $titleBackup
    TitleOriginalCount = $originalTitleCount
    TitleFinalCount = $targetTitle.Count
    TitleCopiedIndices = '26,411'
    TitleTextlessIndices = '899,900,901,902,903'
    Prguse2Path = $targetPrguse2Path
    Prguse2Backup = $prguse2Backup
    Prguse2OriginalCount = $originalPrguse2Count
    Prguse2FinalCount = $targetPrguse2.Count
    Prguse2TextlessIndices = '1602,1603,1604,1605,1606,1607,1608,1609,1610,1611,1612,1613,1614,1615,1616,1617,1618,1619'
} | ConvertTo-Json -Depth 3
