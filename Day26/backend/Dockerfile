# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project file alone first, so `restore` is cached against dependency changes rather
# than against every source edit. Without this split, touching one .cs file re-downloads
# the whole package graph on every build.
COPY QuotesApi.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Container Apps routes to this port; it must match --target-port on the ingress.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# SQLite writes quotes.db into the content root, and a container filesystem does not
# survive a revision restart. That is accepted for this exercise — Day 17 is about the
# deployment and the identity wiring, not about durability. Data resets when the revision
# does, which is why verify.sh treats an empty list as a valid empty state rather than a
# failure.
EXPOSE 8080
ENTRYPOINT ["dotnet", "QuotesApi.dll"]
