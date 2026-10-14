// pm2 config for the always-on rot watcher. Start with:  pm2 start ecosystem.config.cjs
module.exports = {
  apps: [
    {
      name: "rot-watch",
      script: "./rot-watch.sh",
      interpreter: "bash",
      cwd: "/Users/ethanshargorodsky/rot",
      autorestart: true,        // the script loops itself; this just restarts it if it ever dies
      max_restarts: 50,
      restart_delay: 5000,
      out_file: "/Users/ethanshargorodsky/rot/.pm2-out.log",
      error_file: "/Users/ethanshargorodsky/rot/.pm2-err.log",
      time: true,
    },
  ],
};
