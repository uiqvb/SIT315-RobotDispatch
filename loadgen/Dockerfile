# Load generator image, built from the repo root: docker build -f loadgen/Dockerfile -t robot-loadgen .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish loadgen/RobotLoadGen/RobotLoadGen.csproj -c Release -o /out

# aspnet runtime, because the generator reuses the API's credential hasher.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /loadgen
COPY --from=build /out .
# The schema files, so `init --repo /loadgen` can load a fresh benchmark database.
COPY Infrastructure/DataAccess/Database ./Infrastructure/DataAccess/Database
ENTRYPOINT ["dotnet", "RobotLoadGen.dll"]
