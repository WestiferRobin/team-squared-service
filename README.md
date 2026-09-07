# Team Squared Service

Backend application for the Team Squared CS 514 database product project.

## Responsibilities

- .NET Web API
- Domain and business logic
- Relational database access
- Entity Framework Core models and migrations
- Database seed data
- CRUD and analytical database operations
- External football-data integrations
- Unit and integration tests

## Architecture

This repository is the authoritative backend implementation.

The frontend in `team-squared-app` communicates with this service through HTTP/JSON APIs.

The existing `RoadToTheFinal` Python implementation is the legacy reference for prediction logic, football data handling, and existing product behavior. Functionality will be migrated incrementally and verified before legacy code is retired.
