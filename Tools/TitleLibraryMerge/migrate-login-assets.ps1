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
                Layout = "$width,$height,$x,$y,$shadowX,$shadowY,$shadow,$maskSignature"
                Width = $width
                Height = $height
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
    $headerLength = 12 + ($Library.Count * 4)
    $frameOffset = $headerLength
    foreach ($entry in $Entries) { $frameOffset += $entry.Bytes.Length }

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($Library.Version)
        $writer.Write($Library.Count)
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

function Migrate-Indices([string]$LibraryName, [int[]]$Indices) {
    $targetPath = [IO.Path]::GetFullPath((Join-Path $TargetDataDirectory "$LibraryName.Lib"))
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $ChineseDataDirectory "$LibraryName.Lib"))
    if (-not [IO.File]::Exists($targetPath)) { throw "Target library not found: $targetPath" }
    if (-not [IO.File]::Exists($sourcePath)) { throw "Chinese library not found: $sourcePath" }

    $target = Read-Library $targetPath
    $source = Read-Library $sourcePath
    if ($target.Version -ne $source.Version) {
        throw "Version mismatch for $LibraryName`: target $($target.Version), source $($source.Version)"
    }

    $selected = [object[]]::new($target.Count)
    for ($index = 0; $index -lt $target.Count; $index++) { $selected[$index] = $target.Entries[$index] }
    $changed = [Collections.Generic.List[int]]::new()
    foreach ($index in $Indices) {
        if ($index -ge $target.Count -or $index -ge $source.Count) {
            throw "$LibraryName index $index is outside target/source library bounds"
        }
        $selected[$index] = $source.Entries[$index]
        if (-not (Test-BytesEqual $target.Entries[$index].Bytes $source.Entries[$index].Bytes)) {
            $changed.Add($index)
        }
    }

    if ($changed.Count -eq 0) {
        return [pscustomobject]@{ Library = $LibraryName; ChangedIndices = ''; Backup = ''; Target = $targetPath }
    }

    $temporaryPath = "$targetPath.login.tmp"
    $backupPath = "$targetPath.before-login-$(Get-Date -Format 'yyyyMMdd-HHmmssfff').bak"
    try {
        Write-Library $target $selected $temporaryPath
        $validation = Read-Library $temporaryPath
        if ($validation.Version -ne $target.Version -or $validation.Count -ne $target.Count) {
            throw "Validation failed for $temporaryPath"
        }
        foreach ($index in $Indices) {
            if (-not (Test-BytesEqual $validation.Entries[$index].Bytes $source.Entries[$index].Bytes)) {
                throw "Validation failed for $LibraryName index $index"
            }
        }

        [IO.File]::Replace($temporaryPath, $targetPath, $backupPath)
        return [pscustomobject]@{
            Library = $LibraryName
            ChangedIndices = ($changed -join ',')
            Backup = $backupPath
            Target = $targetPath
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) { [IO.File]::Delete($temporaryPath) }
    }
}

Migrate-Indices 'Title' @(30, 31, 32)
Migrate-Indices 'Prguse' @(50, 63)
