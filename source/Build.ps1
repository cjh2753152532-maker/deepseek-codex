$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$outputDirectory = Split-Path -Parent $source
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

$buildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('DeepSeekForCodex.Build.' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($buildDirectory)
$mcpExe = Join-Path $buildDirectory 'DeepSeekMcp.exe'
$setupExe = Join-Path $buildDirectory 'DeepSeekForCodex.Setup.exe'

try {
    Push-Location -LiteralPath $source
    try {
        $common = @('/nologo', '/utf8output', '/optimize+', '/codepage:65001',
            '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll',
            '/reference:System.Security.dll')
        & $compiler @common '/target:exe' "/out:$mcpExe" 'DeepSeekApi.cs' 'Mcp.cs'
        if ($LASTEXITCODE -ne 0) { throw 'The MCP server build failed.' }

        $payloadFiles = @('marketplace.json', 'plugin.json', 'mcp.json', 'compat.json', 'compat-mcp.json',
            'SKILL.md', 'openai.yaml')
        $resources = foreach ($asset in $payloadFiles) {
            "/resource:payload\$asset,$asset"
        }
        $setupArgs = @('/target:winexe', "/out:$setupExe", '/reference:System.Windows.Forms.dll',
            '/reference:System.Drawing.dll', "/resource:$mcpExe,DeepSeekMcp.exe")
        & $compiler @common @setupArgs @resources 'DeepSeekApi.cs' 'Setup.cs'
        if ($LASTEXITCODE -ne 0) { throw 'The Windows installer build failed.' }

        $destination = Join-Path $outputDirectory 'DeepSeekForCodex.Setup.exe'
        Copy-Item -LiteralPath $setupExe -Destination $destination -Force
        Get-Item -LiteralPath $destination | Select-Object FullName, Length
        Get-FileHash -LiteralPath $destination -Algorithm SHA256
    }
    finally { Pop-Location }
}
finally {
    [IO.File]::Delete($setupExe)
    [IO.File]::Delete($mcpExe)
    [IO.Directory]::Delete($buildDirectory)
}
