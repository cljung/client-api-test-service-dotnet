# client-api-test-service-dotnet
test service for DID in aspnet core 3.1

How to Develop
---
```
Build with Asp.Net Core 3.1.
Launch
Browse to http://localhost:5001 
```

How to Dockerfile
---

To run it locally with Docker
```
docker build -t client-api-test-service-dotnet:v1.0 .
docker run --rm -it -p 8080:80 client-api-test-service-dotnet:v1.0

# browse to
http://localhost
```
