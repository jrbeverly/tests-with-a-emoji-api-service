.PHONY: build test format dev smoke-dynamo

# ==============================================================================
# Emoji API Service — Development Targets
# ==============================================================================
SOLUTION ?= EmojiService.slnx

# DynamoDB Local endpoint for all local development
DYNAMO_ENDPOINT ?= http://localhost:8000
AWS_REGION      ?= us-east-1
# Fake credentials — DynamoDB Local accepts any non-empty values
export AWS_ACCESS_KEY_ID     ?= fake
export AWS_SECRET_ACCESS_KEY ?= fake

## build — Compile the solution
build:
	dotnet build $(SOLUTION)

## test — Run all tests
test:
	dotnet test $(SOLUTION)

## format — Format all source files
format:
	dotnet format $(SOLUTION)

## dev — Run the API project locally
dev:
	dotnet run --project src/EmojiService/EmojiService.Api/EmojiService.Api.csproj

## smoke-dynamo — Verify DynamoDB Local connectivity (list tables)
smoke-dynamo:
	aws dynamodb list-tables \
		--endpoint-url "$(DYNAMO_ENDPOINT)" \
		--region "$(AWS_REGION)"
