// Package core manages an embedded Clash/Mihomo core as the client's traffic
// engine. It owns the core binary, the working directory (config + geodata),
// the child process, and the host-side integration knobs (system proxy, TUN).
//
// The package is transport-agnostic: it takes a complete mihomo config.yaml as
// bytes and does not know about WARP, WireGuard, AmneziaWG or MASQUE. The
// caller (package main) renders those proxies from warpscout's engine.
package core

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"sync"
	"time"
)

// Defaults for the managed config, matching what every Clash GUI client uses.
const (
	DefaultMixedPort = 7890
	DefaultAPIPort   = 9090
	ConfigName       = "config.yaml"
	geoSiteName      = "geosite.dat"
	geoIPName        = "geoip.dat"
)

// Config drives a Core.
type Config struct {
	// BinaryPath is the absolute path to the mihomo core executable.
	BinaryPath string
	// WorkDir is where config.yaml and geodata live.
	WorkDir string
	// MixedPort is the local HTTP+SOCKS port handed to system apps.
	MixedPort int
	// APIPort is the external-controller REST port (status / hot-reload).
	APIPort int
	// TUN enables the TUN inbound (needs admin + wintun on Windows).
	TUN bool
	// TieCoreToClient, when true, ties the core's lifetime to this process
	// (Windows job object, kill-on-close): a GUI killed mid-session must
	// never leave a stray core holding the ports. The CLI connect leaves
	// the core running on purpose — its whole contract is a connect command
	// that returns while the proxy keeps serving, with disconnect as a
	// separate invocation that reaches the core through the recorded pid.
	TieCoreToClient bool
	// LogWriter, when non-nil, receives a copy of everything the core process
	// writes to stdout/stderr (the GUI log panel). nil keeps the plain
	// passthrough to this process's own stdout.
	LogWriter io.Writer
}

// stdoutIsConsole reports whether our stdout is a real console. Only a console
// can be handed to a long-lived child safely: it has no reader that waits for
// EOF, unlike a pipe or a redirect.
func stdoutIsConsole() bool {
	fi, err := os.Stdout.Stat()
	if err != nil {
		return false
	}
	return fi.Mode()&os.ModeCharDevice != 0
}

// coreLogFile opens the file a CLI-mode core's output goes to, or returns nil
// when the console can take it directly. Callers defer Close: exec duplicates
// the handle for the child, so the parent's copy is done with once Start returns.
func coreLogFile(workDir string) *os.File {
	if stdoutIsConsole() {
		return nil
	}
	f, err := os.OpenFile(filepath.Join(workDir, "core.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return nil
	}
	return f
}

// Core is a running (or runnable) mihomo instance.
type Core struct {
	cfg    Config
	mu     sync.Mutex
	cmd    *exec.Cmd
	done   chan struct{}
	cancel context.CancelFunc
}

// New validates and prepares a Core. It does not start anything.
func New(cfg Config) (*Core, error) {
	if cfg.BinaryPath == "" {
		return nil, errors.New("core: empty BinaryPath")
	}
	if cfg.WorkDir == "" {
		return nil, errors.New("core: empty WorkDir")
	}
	if cfg.MixedPort == 0 {
		cfg.MixedPort = DefaultMixedPort
	}
	if cfg.APIPort == 0 {
		cfg.APIPort = DefaultAPIPort
	}
	abs, err := filepath.Abs(cfg.BinaryPath)
	if err != nil {
		return nil, err
	}
	cfg.BinaryPath = abs
	return &Core{cfg: cfg}, nil
}

// ConfigPath returns where the running config is (or will be) written.
func (c *Core) ConfigPath() string {
	return filepath.Join(c.cfg.WorkDir, ConfigName)
}

// WriteConfig persists config bytes atomically into the work dir.
func (c *Core) WriteConfig(configYAML []byte) error {
	if err := os.MkdirAll(c.cfg.WorkDir, 0o755); err != nil {
		return fmt.Errorf("core: workdir: %w", err)
	}
	path := c.ConfigPath()
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, configYAML, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// Running reports whether the core process is alive.
func (c *Core) Running() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.cmd != nil
}

// PID returns the OS process id of the running core, or 0 when it has not
// been started (or has already exited) in this instance.
func (c *Core) PID() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.cmd == nil || c.cmd.Process == nil {
		return 0
	}
	return c.cmd.Process.Pid
}

// Start launches the core pointed at cfg.WorkDir. It blocks only until the
// process is spawned; use WaitReady to await readiness.
func (c *Core) Start() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.cmd != nil {
		return errors.New("core: already running")
	}
	if err := os.MkdirAll(c.cfg.WorkDir, 0o755); err != nil {
		return fmt.Errorf("core: workdir: %w", err)
	}
	// Nothing may be listening on the core's ports when the new process binds
	// them. A leftover core that was killed but has not finished dying keeps
	// 7890/9090 bound; the new mihomo then fails its bind yet does NOT exit,
	// WaitReady and the tunnel verification get answered by the dying ghost,
	// and the connect reports success over a port that goes dark moments
	// later. Waiting for the ports to be free closes that whole failure class
	// no matter who left the listener behind.
	if err := waitPortFree("127.0.0.1", c.cfg.MixedPort, 5*time.Second); err != nil {
		return fmt.Errorf("core: mixed port: %w", err)
	}
	if err := waitPortFree("127.0.0.1", c.cfg.APIPort, 5*time.Second); err != nil {
		return fmt.Errorf("core: api port: %w", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cmd := exec.CommandContext(ctx, c.cfg.BinaryPath, "-d", c.cfg.WorkDir)
	cmd.Dir = c.cfg.WorkDir
	// The core logs to stdout/stderr. With a GUI LogWriter attached it is the
	// ONLY sink: os.Stdout may be a dead handle in the windowsgui build, and
	// one failing writer in a MultiWriter stops the exec copier goroutine,
	// which fills the pipe and freezes the core. The CLI keeps its console.
	if c.cfg.LogWriter != nil {
		cmd.Stdout = c.cfg.LogWriter
		cmd.Stderr = c.cfg.LogWriter
	} else if f := coreLogFile(c.cfg.WorkDir); f != nil {
		// stdout is not a console: hand the core a log file, never our own
		// stdout. A child that inherited a pipe's write end keeps it open after
		// this process exits, so the reader on the other side (a script, an IDE
		// task, `xehn connect | tee log`) blocks until the core dies —
		// a connect that "hangs" despite having succeeded.
		defer f.Close()
		cmd.Stdout = f
		cmd.Stderr = f
	} else {
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
	}
	hideConsole(cmd)
	if err := cmd.Start(); err != nil {
		cancel()
		return fmt.Errorf("core: start %s: %w", c.cfg.BinaryPath, err)
	}
	// GUI mode (and only GUI mode — see TieCoreToClient) ties the core to
	// this process's lifetime so a killed client can never orphan it.
	if c.cfg.TieCoreToClient {
		spawnWatchdog(cmd.Process.Pid)
	}
	c.cmd = cmd
	c.cancel = cancel
	c.done = make(chan struct{})
	go func() {
		_ = cmd.Wait()
		close(c.done)
		c.mu.Lock()
		c.cmd = nil
		c.mu.Unlock()
	}()
	return nil
}

// WaitReady polls the external-controller until it answers or ctx expires.
func (c *Core) WaitReady(ctx context.Context) error {
	base := "http://127.0.0.1:" + strconv.Itoa(c.cfg.APIPort) + "/version"
	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf("core: not ready within deadline: %w", ctx.Err())
		default:
		}
		if pingController(base) {
			return nil
		}
		time.Sleep(150 * time.Millisecond)
	}
}

// Stop terminates the core process and waits for it to exit.
func (c *Core) Stop() error {
	c.mu.Lock()
	cmd := c.cmd
	cancel := c.cancel
	c.mu.Unlock()
	if cmd == nil {
		return nil
	}
	if cancel != nil {
		cancel()
	}
	if c.done != nil {
		select {
		case <-c.done:
		case <-time.After(3 * time.Second):
			_ = cmd.Process.Kill()
			<-c.done
		}
	}
	return nil
}

// waitPortFree blocks until nothing accepts connections on host:port, or the
// timeout expires. On loopback a refused dial is the definitive "no listener"
// answer, so anything that keeps accepting past the deadline is a leftover
// process the new core cannot coexist with — better a clear error than a
// bind-failed zombie core behind a "connected" light.
func waitPortFree(host string, port int, timeout time.Duration) error {
	if port <= 0 {
		return nil
	}
	addr := net.JoinHostPort(host, strconv.Itoa(port))
	deadline := time.Now().Add(timeout)
	for {
		conn, err := net.DialTimeout("tcp", addr, 250*time.Millisecond)
		if err != nil {
			return nil
		}
		_ = conn.Close()
		if time.Now().After(deadline) {
			return fmt.Errorf("port %d still in use (a previous core may not have exited)", port)
		}
		time.Sleep(150 * time.Millisecond)
	}
}

// ProxyDelay asks the running external-controller for the round-trip latency
// (ms) of the named proxy or proxy-group, through the given test URL. The GUI
// poller feeds this into its latency chart.
func ProxyDelay(apiPort int, name, testURL string) (int64, error) {
	base := "http://127.0.0.1:" + strconv.Itoa(apiPort) + "/proxies/" + name + "/delay"
	client := httpClient(6 * time.Second)
	u := base + "?url=" + url.QueryEscape(testURL) + "&timeout=5000"
	resp, err := client.Get(u)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return 0, fmt.Errorf("core: delay %d", resp.StatusCode)
	}
	var body struct {
		Delay int64 `json:"delay"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return 0, err
	}
	return body.Delay, nil
}

// Traffic samples the core's live throughput counters once. The /traffic
// endpoint streams one {"up":..,"down":..} object per second (bytes/sec), so
// a GET that decodes a single JSON value and hangs up yields one rate sample.
func Traffic(apiPort int) (up, down int64, err error) {
	u := "http://127.0.0.1:" + strconv.Itoa(apiPort) + "/traffic"
	client := httpClient(3 * time.Second)
	resp, err := client.Get(u)
	if err != nil {
		return 0, 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return 0, 0, fmt.Errorf("core: traffic %d", resp.StatusCode)
	}
	var body struct {
		Up   int64 `json:"up"`
		Down int64 `json:"down"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return 0, 0, err
	}
	return body.Up, body.Down, nil
}

func pingController(base string) bool {
	client := httpClient(1 * time.Second)
	resp, err := client.Get(base)
	if err != nil {
		return false
	}
	defer resp.Body.Close()
	return resp.StatusCode < 500
}
