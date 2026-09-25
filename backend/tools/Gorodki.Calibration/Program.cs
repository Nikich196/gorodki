// Разбор записанных забегов, половина сервера (docs/guides/calibration-replay.md, полевой тест №1).
//
// Вход — петли, найденные детектором телефона (ios/Tools/LoopReplay), вместе с точками забега. Каждая петля проходит
// тот же код, что на сервере: судья отрезков → кольцо петли → шаг A (контур P) с каждой парой A_min × R_min.
// Выход — таблицы Markdown на экран и HTML с картинками «след + контур».
//
// Обычно запускается скриптом calibrate.sh вместе с половиной телефона:
//   bash backend/tools/Gorodki.Calibration/calibrate.sh <мои-данные.json> [параметры]

using Gorodki.Calibration;

try
{
    var options = Options.Parse(args);
    var file = ReplayFile.Read(File.ReadAllText(options.Loops));
    var runs = Calibration.Evaluate(file, options.Shapes);
    Console.Write(Report.Markdown(file, runs, options));
    File.WriteAllText(options.Out, Report.Html(file, runs, options));
    Console.WriteLine();
    Console.WriteLine($"Картинки «след + контур»: {options.Out}");
    return 0;
}
catch (UsageException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}
