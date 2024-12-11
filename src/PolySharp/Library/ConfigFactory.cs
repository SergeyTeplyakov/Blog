using Core;

namespace Factory;

public static class ConfigFactory
{
    public static Config Create(int value) => new () { X = value };
}