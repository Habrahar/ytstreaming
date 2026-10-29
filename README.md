# ytstreaming

Домашний плеер YouTube Music: сервер получает музыку, а ты слушаешь её в браузере
на телефоне. Есть поиск, обложки, лайки, плейлисты и автоматическое радио.
Аккаунт не нужен. Библиотека хранит ссылки на песни; эфир общий для всех устройств.

## Запуск

Нужны Linux x64/ARM64, Git, Docker с Compose и доступ сервера к YouTube.

```sh
git clone https://github.com/Habrahar/ytstreaming.git
cd ytstreaming
cp .env.example .env
```

В `.env` укажи локальный IP сервера в `BIND_ADDRESS`.

На Raspberry Pi с 1 ГБ памяти сначала собери приложение на ПК с .NET SDK 8
из копии этого проекта и передай результат на Pi:

```powershell
./scripts/publish.ps1
ssh user@server 'mkdir -p ~/ytstreaming/artifacts'
scp -r ./artifacts/publish user@server:ytstreaming/artifacts/
```

Замени `user@server` своим подключением. Для этого варианта в `.env` на Pi
укажи `DOCKERFILE=Dockerfile.runtime`. На остальных серверах этот шаг можно пропустить.

Запусти из папки проекта на сервере:

```sh
docker compose up -d --build provider bridge
```

Открой `http://<IP-сервера>:8090/` на телефоне в той же сети, найди песню и нажми на неё.
