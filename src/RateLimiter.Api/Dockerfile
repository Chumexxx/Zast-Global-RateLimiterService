# ============================================================================
# Stage 1: Build
# ============================================================================
# We use the full SDK image here because we need the compiler - this stage's
# output is thrown away after publishing, so its size doesn't matter.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy ONLY the .csproj files first, then restore. Docker caches each layer
# based on the files it copies - as long as none of these .csproj files
# change, `docker compose up --build` reuses this (slow, network-heavy)
# restore layer instead of re-downloading every NuGet package on every
# build. This is the single biggest thing that keeps rebuild times fast
# during development.
COPY ["src/RateLimiter.Api/RateLimiter.Api.csproj", "src/RateLimiter.Api/"]
COPY ["src/RateLimiter.Infrastructure/RateLimiter.Infrastructure.csproj", "src/RateLimiter.Infrastructure/"]
COPY ["src/RateLimiter.Domain/RateLimiter.Domain.csproj", "src/RateLimiter.Domain/"]
RUN dotnet restore "src/RateLimiter.Api/RateLimiter.Api.csproj"

# NOW copy everything else (actual source code) and build. Changes to .cs
# files invalidate only this layer onward, not the restore above.
COPY . .
WORKDIR /src/src/RateLimiter.Api
RUN dotnet publish "RateLimiter.Api.csproj" -c Release -o /app/publish --no-restore

# ============================================================================
# Stage 2: Runtime
# ============================================================================
# The ASP.NET runtime image (not the SDK) is used for the final image - it
# doesn't contain the compiler/build tools, so it's meaningfully smaller and
# has a smaller attack surface, which matters for anything internet-facing.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Run as a non-root user rather than the container default (root). A small
# but real security-hardening step: if the app were ever compromised, the
# attacker doesn't automatically get root inside the container.
RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

# Matches ASPNETCORE_URLS=http://+:8080 set in docker-compose.yml, and the
# port Nginx's upstream block points at.
EXPOSE 8080

ENTRYPOINT ["dotnet", "RateLimiter.Api.dll"]
