using EmojiService.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EmojiService.Api.Routes.Assets.v1;

public static class GetAssetRoute
{
    public static class Registration
    {
        public static RouteHandlerBuilder Map(RouteGroupBuilder group)
        {
            return group
                .MapGet("/{uid}", Handler.HandleAsync)
                .WithName("GetAsset")
                .WithTags("Assets")
                .Produces(StatusCodes.Status200OK)
                .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
                .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);
        }
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(
            [FromRoute] string uid,
            IEmojiRepository repository,
            IAssetStore assetStore
        )
        {
            var uidResult = EmojiUid.Create(uid);
            if (uidResult.IsFailure)
                return TypedResults.BadRequest(
                    new ProblemDetails
                    {
                        Status = 400,
                        Title = uidResult.Error!.Code,
                        Detail = uidResult.Error.Message,
                    }
                );

            var emoji = await repository.GetByUidAsync(uidResult.Value!);
            if (emoji is null)
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Emoji with UID '{uid}' not found",
                    }
                );

            try
            {
                var asset = await assetStore.GetAsync(uidResult.Value!.Value);
                return TypedResults.File(asset.Content, asset.ContentType);
            }
            catch (FileNotFoundException)
            {
                return TypedResults.NotFound(
                    new ProblemDetails
                    {
                        Status = 404,
                        Title = "Not Found",
                        Detail = $"Asset not found for UID '{uid}'",
                    }
                );
            }
        }
    }
}
