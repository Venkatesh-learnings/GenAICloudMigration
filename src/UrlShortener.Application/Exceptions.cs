namespace UrlShortener.Application;

public class InvalidUrlException(string url) : Exception($"'{url}' is not a valid absolute http/https URL.");

public class AliasAlreadyTakenException(string alias) : Exception($"Alias '{alias}' is already in use.");

public class ShortCodeExhaustionException()
    : Exception("Unable to generate a unique short code after the maximum number of attempts.");
