"""End-to-end collectors against isolated source databases / transcripts."""
import json, os, pathlib, sqlite3, struct, subprocess, tempfile, unittest, uuid
ROOT = pathlib.Path(__file__).resolve().parents[1]
CLI = ROOT / 'src/Orisumi.Cli/bin/Debug/net11.0/Orisumi'

class CaptureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='orisumi-facts-')
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.store = self.root / 'store'
    def run_capture(self, source, *args):
        env = dict(os.environ, CODEX_HOME=str(self.root), CLAUDE_CONFIG_DIR=str(self.root))
        result = subprocess.run([str(CLI),'--store',str(self.store),source,'capture',*args],env=env,capture_output=True,text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
    def db(self, source):
        db=sqlite3.connect(self.store/'raw'/f'{source}.sqlite');self.addCleanup(db.close);return db
    def count(self, db, table): return db.execute(f'SELECT count(*) FROM {table}').fetchone()[0]
    def test_codex_independent_changes_and_repeated_import(self):
        state=sqlite3.connect(self.root/'state_5.sqlite');self.addCleanup(state.close)
        state.executescript('''CREATE TABLE threads(id TEXT,name TEXT,title TEXT,cwd TEXT,created_at_ms INT,created_at INT,updated_at_ms INT,updated_at INT,archived INT,history_mode TEXT,rollout_path TEXT);
        INSERT INTO threads VALUES('t','A','fallback','/work',1000,1,2000,2,0,'paginated','missing');''')
        history=sqlite3.connect(self.root/'thread_history_1.sqlite');self.addCleanup(history.close)
        history.executescript('''CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,started_at INT,completed_at INT,status TEXT);
        INSERT INTO thread_turns VALUES('t','turn',1,NULL,'inProgress');
        CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_id TEXT,item_type TEXT,created_at_ms INT,rollout_ordinal INT,item_json TEXT);
        INSERT INTO thread_items VALUES('t','turn','m','agentMessage',1000,1,'{"text":"first"}');''')
        rollout=self.root/'rollout.jsonl'
        def usage(output):return {'type':'token_usage_record','timestamp':'2026-01-01T00:00:00Z',
            'payload':{'thread_id':'t','turn_id':'turn','response_id':'response','usage':{'input_tokens':10,'output_tokens':output}}}
        rollout.write_text(json.dumps(usage(1))+'\n')
        state.execute('UPDATE threads SET rollout_path=?',(str(rollout),));state.commit()
        self.run_capture('codex','--all');db=self.db('codex')
        self.run_capture('codex','--all')
        self.assertEqual(self.count(db,'thread_title_observations'),1)
        self.assertEqual(self.count(db,'message_content_observations'),1)
        with rollout.open('a') as stream:stream.write(json.dumps(usage(2))+'\n')
        state.execute("UPDATE threads SET name='B'");state.commit()
        history.execute("UPDATE thread_items SET item_json='final'");history.execute("UPDATE thread_turns SET completed_at=3,status='completed'");history.commit()
        self.run_capture('codex','--all')
        state.execute("UPDATE threads SET name='A'");state.commit();self.run_capture('codex','--all')
        self.assertEqual(db.execute('SELECT title FROM thread_title_observations ORDER BY observation_id').fetchall(),[('A',),('B',),('A',)])
        self.assertEqual(self.count(db,'response_token_usage_observations'),2)
        self.assertEqual(db.execute('SELECT total_output_tokens FROM response_token_usage').fetchone(),(2,))
        self.assertEqual(self.count(db,'thread_cwd_observations'),1)
        self.assertEqual(self.count(db,'thread_archive_observations'),1)
        self.assertEqual(self.count(db,'message_content_observations'),2)
        self.assertEqual(db.execute('SELECT status,completed_at_utc_ms FROM current_turns').fetchone(),('completed',3000))
        self.assertEqual(db.execute('SELECT item_json FROM current_messages').fetchone(),('final',))
        self.assertEqual(db.execute('SELECT count(*) FROM capture_runs WHERE stopped_at_utc_ms IS NOT NULL').fetchone()[0],4)
    def test_claude_source_order_content_revisions_and_no_invented_title_time(self):
        directory=self.root/'projects'/'test';directory.mkdir(parents=True);path=directory/'session.jsonl'
        def title(value):return {'type':'custom-title','sessionId':'s','customTitle':value}
        def message(text, output):return {'type':'assistant','sessionId':'s','uuid':'m','timestamp':'2026-01-01T00:00:00Z','cwd':'/work',
            'message':{'id':'r','model':'model','content':[{'type':'text','text':text}], 'usage':{'input_tokens':10,'output_tokens':output}}}
        rows=[title('A'),message('first',1),title('B'),message('final',2),title('A')]
        path.write_text(''.join(json.dumps(r)+'\n' for r in rows))
        self.run_capture('claude');db=self.db('claude');self.run_capture('claude')
        self.assertEqual(db.execute('SELECT title FROM session_title_observations ORDER BY observation_id').fetchall(),[('A',),('B',),('A',)])
        self.assertEqual(self.count(db,'messages'),1)
        self.assertEqual(self.count(db,'message_content_observations'),2)
        self.assertEqual(self.count(db,'response_token_usage_observations'),2)
        self.assertEqual(db.execute('SELECT total_output_tokens FROM response_token_usage').fetchone(),(2,))
        self.assertEqual(self.count(db,'source_records'),5)
        self.assertEqual(db.execute('SELECT title FROM current_sessions').fetchone(),('A',))
    def browser(self, batch):
        payload=json.dumps({'profileId':'11111111-1111-4111-8111-111111111111',**batch}).encode()
        result=subprocess.run([str(CLI),'--store',str(self.store),'browser','native-host'],input=struct.pack('<I',len(payload))+payload,capture_output=True)
        self.assertEqual(result.returncode,0,result.stderr)
        reply=json.loads(result.stdout[4:]);self.assertTrue(reply['ok'],result.stderr);return reply
    def test_tabs_title_only_navigation_focus_and_retry(self):
        run=str(uuid.uuid4())
        def event(seq,**changes):return dict(eventId=str(uuid.uuid4()),captureSessionId=run,runStartedAtUtcMs=1000,sessionSequence=seq,
            observedAtUtcMs=1000+seq,eventKind='snapshot',windowId=1,tabId=2,url='https://example.com',title='A',**changes)
        first=event(1); second={**event(2),'title':'B','eventKind':'title_changed'}
        third={**event(3),'title':'C','url':'https://example.com/other','eventKind':'url_changed'}
        blur={**event(4),'tabId':None,'windowId':None,'url':None,'title':None,'eventKind':'window_blurred'}
        self.browser({'activeTabEvents':[first,second,third,blur]});self.browser({'activeTabEvents':[second]})
        db=self.db('browser')
        self.assertEqual(self.count(db,'active_tab_focus_events'),2)
        self.assertEqual(self.count(db,'tab_url_observations'),2)
        self.assertEqual(self.count(db,'tab_title_observations'),3)
        self.assertEqual(self.count(db,'tab_observations'),4)
        self.assertEqual(db.execute('SELECT title FROM active_tab_state_history ORDER BY run_sequence').fetchall(),[('A',),('B',),('C',),(None,)])
    def test_download_state_and_path_changes_do_not_copy_resource(self):
        run=str(uuid.uuid4())
        def event(seq,fields):return dict(eventId=str(uuid.uuid4()),runId=run,runStartedAtUtcMs=1000,runSequence=seq,observedAtUtcMs=1000+seq,kind='changed',downloadId=1,fields=fields)
        initial=event(1,{'url':'https://example.com/file','startedAtUtcMs':1000,'state':'in_progress','filename':'/tmp/file'})
        moved=event(2,{'filename':'/tmp/new'})
        done=event(3,{'state':'complete','endedAtUtcMs':1003})
        self.browser({'downloads':[initial,moved,done]});self.browser({'downloads':[done]})
        db=self.db('browser')
        self.assertEqual(self.count(db,'download_resource_observations'),1)
        self.assertEqual(self.count(db,'download_state_observations'),2)
        self.assertEqual(self.count(db,'download_path_observations'),2)
        self.assertEqual(db.execute('SELECT state,local_path,url,ended_at_utc_ms FROM current_downloads').fetchone(),('complete','/tmp/new','https://example.com/file',1003))
        self.assertEqual(db.execute('SELECT state,local_path FROM download_history ORDER BY observation_id').fetchall(),[('in_progress','/tmp/file'),('in_progress','/tmp/new'),('complete','/tmp/new')])

    @unittest.skipUnless(os.uname().sysname == 'Darwin', 'macOS native collector')
    def test_macos_focus_title_process_reuse_and_channel_gaps(self):
        native = ROOT / 'src/Collectors/Orisumi.Collectors.MacOS/Native'
        main = self.root / 'main.swift'
        main.write_text((ROOT / 'tests/macos_facts.swift').read_text())
        binary = self.root / 'facts'
        subprocess.run(['xcrun','swiftc',str(main),*[str(native / name) for name in ['ForegroundDatabase.swift','InputEventObserver.swift','KeyboardPressAccumulator.swift']],'-lsqlite3','-o',str(binary)],check=True,capture_output=True)
        path=self.root/'macos.sqlite'
        subprocess.run([str(binary),str(path)],check=True,capture_output=True)
        db=sqlite3.connect(path);self.addCleanup(db.close)
        self.assertEqual(self.count(db,'process_instances'),2)
        self.assertEqual(self.count(db,'window_focus_instances'),2)
        self.assertEqual(self.count(db,'focused_window_events'),3)
        self.assertEqual(self.count(db,'window_title_observations'),3)
        self.assertEqual(db.execute('SELECT window_title FROM window_history ORDER BY run_sequence').fetchall(),[('A',),('B',),('C',),(None,)])
        self.assertEqual(db.execute('SELECT available FROM capture_channel_events ORDER BY run_sequence').fetchall(),[(1,),(0,),(1,)])
        self.assertEqual(db.execute('SELECT capture_windows,capture_input,stopped_at_utc_ms IS NOT NULL FROM capture_runs').fetchone(),(1,1,1))
        self.assertEqual(db.execute('PRAGMA foreign_key_check').fetchall(),[])

if __name__=='__main__': unittest.main()
