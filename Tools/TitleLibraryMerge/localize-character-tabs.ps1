param(
    [Parameter(Mandatory = $true)]
    [string[]]$TitleLibraries
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
        for ($index = 0; $index -lt $count; $index++) { $offsets[$index] = $reader.ReadInt32() }

        $entries = [object[]]::new($count)
        for ($index = 0; $index -lt $count; $index++) {
            $start = $offsets[$index]
            $end = if ($index + 1 -lt $count) { $offsets[$index + 1] } else { $frameOffset }
            $stream.Position = $start
            $entry = $reader.ReadBytes($end - $start)
            if ($entry.Length -ne $end - $start) { throw "Truncated entry $index in $Path" }
            $entries[$index] = $entry
        }

        $stream.Position = $frameOffset
        $frames = $reader.ReadBytes($bytes.Length - $frameOffset)
        if ($frames.Length -ne $bytes.Length - $frameOffset) { throw "Truncated frame data in $Path" }
        return [pscustomobject]@{ Version = $version; Count = $count; Entries = $entries; Frames = $frames }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Expand-Entry([byte[]]$Entry) {
    $stream = [IO.MemoryStream]::new($Entry, $false)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $header = $reader.ReadBytes(13)
        $dataLength = $reader.ReadInt32()
        $compressed = $reader.ReadBytes($dataLength)
        $trailing = $reader.ReadBytes([int]($stream.Length - $stream.Position))

        $input = [IO.MemoryStream]::new($compressed, $false)
        $gzip = [IO.Compression.GZipStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
        $output = [IO.MemoryStream]::new()
        try { $gzip.CopyTo($output); $pixels = $output.ToArray() }
        finally { $output.Dispose(); $gzip.Dispose(); $input.Dispose() }

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

function Clear-Text([byte[]]$Pixels, [int]$Width, [int]$Height, [int]$Left, [int]$Top, [int]$Right, [int]$Bottom) {
    if ($Left -le 0 -or $Right -ge $Width - 1 -or $Top -lt 0 -or $Bottom -ge $Height) {
        throw "Invalid text region $Left,$Top-$Right,$Bottom for ${Width}x${Height} image"
    }

    for ($y = $Top; $y -le $Bottom; $y++) {
        $leftSample = (($y * $Width) + ($Left - 1)) * 4
        $rightSample = (($y * $Width) + ($Right + 1)) * 4
        for ($x = $Left; $x -le $Right; $x++) {
            $ratio = ($x - $Left + 1.0) / ($Right - $Left + 2.0)
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
        return ,$entryStream.ToArray()
    }
    finally {
        $writer.Dispose()
        $entryStream.Dispose()
    }
}

function Write-Library($Library, [string]$Path) {
    $headerLength = 12 + ($Library.Count * 4)
    $frameOffset = $headerLength
    foreach ($entry in $Library.Entries) { $frameOffset += $entry.Length }

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write($Library.Version)
        $writer.Write($Library.Count)
        $writer.Write($frameOffset)
        $offset = $headerLength
        foreach ($entry in $Library.Entries) { $writer.Write($offset); $offset += $entry.Length }
        foreach ($entry in $Library.Entries) { $writer.Write($entry) }
        $writer.Write($Library.Frames)
        $writer.Flush()
        [IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

foreach ($libraryPath in $TitleLibraries) {
    $path = [IO.Path]::GetFullPath($libraryPath)
    if (-not [IO.File]::Exists($path)) { throw "Title library not found: $path" }

    $library = Read-Library $path
    foreach ($index in 500..503) {
        $image = Expand-Entry $library.Entries[$index]
        if ($image.Width -ne 64 -or $image.Height -ne 20) {
            throw "Title[$index] is $($image.Width)x$($image.Height); expected 64x20 in $path"
        }
        Clear-Text $image.Pixels $image.Width $image.Height 5 4 58 17
        $library.Entries[$index] = Compress-Entry $image
    }

    $background = Expand-Entry $library.Entries[504]
    if ($background.Width -ne 264 -or $background.Height -ne 380) {
        throw "Title[504] is $($background.Width)x$($background.Height); expected 264x380 in $path"
    }
    foreach ($left in 8, 70, 132, 194) {
        Clear-Text $background.Pixels $background.Width $background.Height ($left + 5) 74 ($left + 58) 87
    }
    $library.Entries[504] = Compress-Entry $background

    $temporary = "$path.tabs.tmp"
    $backup = "$path.before-character-tabs-$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
    try {
        Write-Library $library $temporary
        $validation = Read-Library $temporary
        if ($validation.Version -ne $library.Version -or $validation.Count -ne $library.Count) {
            throw "Validation failed for $temporary"
        }
        [IO.File]::Replace($temporary, $path, $backup)
        [pscustomobject]@{ Path = $path; Backup = $backup; Count = $validation.Count }
    }
    finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
