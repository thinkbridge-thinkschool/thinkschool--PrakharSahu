# =============================================================================================
# The modular monolith, built as one image.
#
# 23 projects, one deployable. The whole solution is copied and restored, then only the host
# project is published -- project references pull in exactly the modules it needs. `dotnet
# publish` on the host emits one directory containing every module assembly, which is what
# "modular monolith" means at deployment time: the boundaries are compile-time facts, and the
# artefact is a single process.
#
# The build context is the SOLUTION directory (backend/), not the host project. Copying only the
# host project would fail restore on nineteen missing project references.
#
# NOTE: no "copy the .csproj files first, restore, then copy the source" layer-caching trick.
# That pattern needs a recursive glob, and Dockerfile COPY matches with Go's filepath.Match,
# which does NOT support `**`. Written that way it matches nothing and the build fails with
# "no source files were specified" -- which is exactly how the first version of this file was
# wrong. Listing 23 COPY lines by hand would work and would then go stale the day a project is
# added or renamed. One COPY that is always correct beats a cache optimisation that rots.
# =============================================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . ./

RUN dotnet restore src/QuotesApi.Host/QuotesApi.Host.csproj
RUN dotnet publish src/QuotesApi.Host/QuotesApi.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# The assembly is still QuotesApi.dll -- see AssemblyName in QuotesApi.Host.csproj. The
# verification scripts find the process by that name, and so does this entrypoint.
ENTRYPOINT ["dotnet", "QuotesApi.dll"]
