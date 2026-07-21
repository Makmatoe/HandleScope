# Third-party notices

HandleScope currently has no third-party NuGet package dependencies and does
not bundle Sysinternals or other external executables.

The projects build on the Microsoft .NET SDK and Windows platform APIs. A
self-contained release includes .NET runtime components under Microsoft's
applicable license terms. The release staging script copies the exact SDK-level
`LICENSE.txt` and `ThirdPartyNotices.txt` files into the bundle as
`dotnet-LICENSE.txt` and `dotnet-THIRD-PARTY-NOTICES.txt`.

GitHub Actions referenced by repository automation run in GitHub's environment
and are not redistributed as part of HandleScope.
