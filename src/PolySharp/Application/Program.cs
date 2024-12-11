using Core;
using Factory;

Console.WriteLine("Starting...");
var config = new Config() { X = 42 };
config = ConfigFactory.Create(42);
Console.WriteLine("Done!");