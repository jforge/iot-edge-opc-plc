# Use the official .NET 8 SDK image for the build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# Set the working directory inside the container
WORKDIR /app

# Copy the project file(s) and restore dependencies
COPY ./src/*.csproj ./src/
RUN dotnet restore ./src/*.csproj

# Copy the remaining source code
COPY ./src/ ./src/

# Build the project in Release mode and publish the output
COPY ./README.md ./
RUN dotnet publish ./src/*.csproj -c Release -o /app/publish

# Use the official .NET 8 runtime image for the runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Set the working directory inside the container for the runtime stage
WORKDIR /app

# Copy the published files from the build stage
COPY --from=build /app/publish .

# Expose the port for the OPC server (change it to match your server config)
EXPOSE 50000

# Set the entrypoint to run the application
ENTRYPOINT ["dotnet", "opc-plc.dll"]

