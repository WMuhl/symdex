namespace MultiTfm;

public static class App
{
    public static int Go(IWorker worker) => worker.Run(1);
}
