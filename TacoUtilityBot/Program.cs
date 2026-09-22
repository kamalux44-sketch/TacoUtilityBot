using DSharpPlus.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TacoUtilityBot.Commands;
using TacoUtilityBot.Configuration;
using TacoUtilityBot.Models;
using TacoUtilityBot.Services;

var builder = Host.CreateApplicationBuilder(args);

var voiceRelaySection = builder.Configuration.GetSection("VoiceRelay");
var voiceRelayConfig = new VoiceRelayConfig
{
    MaxQueueSize = int.TryParse(voiceRelaySection["MaxQueueSize"], out var maxQueueSize)
        ? maxQueueSize
        : 100
};
var discordSection = builder.Configuration.GetSection("Discord");
voiceRelayConfig.ReceiverToken = discordSection["ReceiverToken"] ?? string.Empty;
voiceRelayConfig.TransmitterToken = discordSection["TransmitterToken"] ?? string.Empty;

builder.Services.AddSingleton(voiceRelayConfig);
builder.Services.AddSingleton<AudioRelayService>();
builder.Services.AddSingleton<ReceiverBotService>();
builder.Services.AddSingleton<TransmitterBotService>();
builder.Services.AddSingleton<RelayCommand>();

using var host = builder.Build();
var receiver = host.Services.GetRequiredService<ReceiverBotService>();
var transmitter = host.Services.GetRequiredService<TransmitterBotService>();
var relay = host.Services.GetRequiredService<AudioRelayService>();

var slashCommands = receiver.Client.UseSlashCommands(new SlashCommandsConfiguration
{
    Services = host.Services
});
slashCommands.RegisterCommands<RelayCommand>();

try
{
    await host.StartAsync();
    var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
    await receiver.StartAsync(stopping);
    await transmitter.StartAsync(stopping);
    await host.WaitForShutdownAsync();
}
finally
{
    relay.StopAll();
    await transmitter.DisposeAsync();
    await receiver.DisposeAsync();
    await host.StopAsync();
}
