[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $PipeName,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $Path,

    [ValidateRange(100, 30000)]
    [int] $TimeoutMilliseconds = 5000
)

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$client = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.',
    $PipeName,
    [System.IO.Pipes.PipeDirection]::Out,
    [System.IO.Pipes.PipeOptions]::Asynchronous)

try {
    $client.Connect($TimeoutMilliseconds)
    $writer = [System.IO.StreamWriter]::new($client)
    try {
        $writer.AutoFlush = $true
        $writer.WriteLine($resolvedPath)
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $client.Dispose()
}

Write-Output "Requested NoteManager folder change to: $resolvedPath"
