// Two real native clients plus the unmodified separately installed provider. No guest or fake player.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
let stage='fixture verification',lastStatus;const passed=[];
process.on('uncaughtException',()=>{console.error('Owned provider-client test failed during '+stage+'. Private logs suppressed.');process.exitCode=1;});
process.on('unhandledRejection',()=>{console.error('Owned provider-client test failed during '+stage+'. Private logs suppressed.');process.exitCode=1;});
const [stateFile,playwrightModule]=process.argv.slice(2), state=JSON.parse(await readFile(stateFile,'utf8'));
if(state.Name!=='mediahub-test-visibility'||state.Port!==18496||!state.LibraryAcceptance?.IntentionalFixtureSetup)throw new Error('Only the owned generated-media fixture is allowed.');
const origin='http://127.0.0.1:18496';
const {chromium}=await import(pathToFileURL(resolve(playwrightModule)).href);
const browser=await chromium.launch({...(process.env.MEDIAHUB_BROWSER_CHANNEL==='chromium'?{}:{channel:'msedge'}),headless:true});
async function login(name,password){
    const page=await(await browser.newContext()).newPage();await page.goto(origin+'/web/');await page.locator('#txtManualName').waitFor({state:'attached'});
    if(!await page.locator('#txtManualName').isVisible()){try{await page.locator('.btnManual').click({timeout:5000});}catch{if(!await page.locator('#txtManualName').isVisible())throw new Error('Native login unavailable');}}
    await page.locator('#txtManualName').waitFor({state:'visible'});await page.locator('#txtManualName').fill(name);await page.locator('#txtManualPassword').fill(password);
    await page.locator('.manualLoginForm button[type="submit"]').click();await page.waitForFunction(()=>!!window.ApiClient?.getCurrentUserId?.()&&/#\/home/.test(location.hash));return page;
}
async function api(page,path,body){const result=await page.evaluate(async({path,body})=>{try{return{Status:200,Data:await window.ApiClient.ajax({type:body===undefined?'GET':'POST',url:window.ApiClient.getUrl('MediaHub/'+path),dataType:'json',contentType:'application/json',data:body===undefined?undefined:JSON.stringify(body)})};}catch(error){return{Status:error.status||0};}},{path,body});lastStatus=result.Status;return result;}
async function admission(page,invite,expected){
    // Public HTTPS is deliberately non-routable in this fixture. Only the bearer fragment is
    // navigated on this known loopback origin to test the provider's own signed-in admission UI.
    // This is NOT evidence of a deployed public HTTPS/proxy invitation flow.
    const fragment=new URL(invite.Url).hash;assert.match(fragment,/^#[a-f0-9]{64}$/i);
    const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/PrivateSyncPlay/JoinInvite',{timeout:20000});
    await page.goto(origin+'/PrivateSyncPlay/Watch'+fragment);const status=(await response).status();lastStatus=status;
    if(expected===200)assert.ok(status>=200&&status<300);else assert.equal(status,expected);
    await page.waitForFunction(()=>!!window.ApiClient?.getCurrentUserId?.());
    assert.equal(await page.evaluate(()=>/^[a-f0-9]{64}$/i.test(location.hash.slice(1))),false);
}
const playing=()=>[...document.querySelectorAll('video')].some(video=>!video.paused&&video.currentTime>.25&&video.readyState>=2&&video.videoWidth>0);
try{
    stage='real owner and administrator participant browsers';const owner=await login('mediahub-viewer',state.ViewerPassword),peer=await login('mediahub-admin',state.AdminPassword);
    assert.equal((await api(owner,'WatchParty')).Data.Active,false);assert.equal((await api(peer,'WatchParty')).Data.Active,false);
    stage='atomic private creation through Media Hub public SDK adapter';const creation=await api(owner,'WatchParty',{Name:'Owned two-client media acceptance'});assert.equal(creation.Status,200);assert.equal(creation.Data.Party.Owner,true);
    const party=creation.Data.Party.PartyId, path='WatchParty/'+party;
    stage='actual provider-native signed-in admission';await admission(peer,creation.Data.Invite,200);
    const joined=await api(peer,'WatchParty');assert.equal(joined.Data.Active,true);assert.equal(joined.Data.PartyId,party);assert.equal(joined.Data.Owner,false);
    assert.equal((await api(owner,'WatchParty')).Data.ViewerCount,2);
    passed.push('real native admission and two memberships');
    stage='administrator participant does not inherit Media Hub owner authority';
    assert.equal((await api(peer,path+'/Invites',{})).Status,403);assert.equal((await api(peer,path+'/End',{})).Status,403);
    passed.push('administrator owner bypass rejected');
    stage='native destination playback cannot bypass a companion party';
    const destinations=await api(owner,'Players');assert.equal(destinations.Status,200);assert.equal(destinations.Data.length,1);
    const preview=await api(owner,'Players/Prepare',{Target:destinations.Data[0].Id,Item:state.LibraryAcceptance.Visible});assert.equal(preview.Status,200);
    assert.equal((await api(owner,'Players/'+preview.Data.Id+'/Confirm',{})).Status,400);assert.equal((await api(owner,'Players/'+preview.Data.Id+'/Cancel',{})).Data.State,'cancelled');
    passed.push('native player party bypass rejected');
    stage='native group playback of generated media in both actual clients';
    await owner.evaluate(id=>{location.hash='#/details?id='+id+'&serverId='+window.ApiClient.serverId();},state.LibraryAcceptance.Visible);
    await owner.locator('.btnPlay').first().click();await owner.waitForFunction(playing,undefined,{timeout:25000});await peer.waitForFunction(playing,undefined,{timeout:25000});
    passed.push('both real clients decoded advancing generated video');
    stage='fresh owner permission snapshot and stale-review rejection';
    const management=await api(owner,path+'/Management');assert.equal(management.Status,200);const account=management.Data.Accounts.find(row=>!row.IsOwner);assert.ok(account);
    assert.equal((await api(owner,path+'/Accounts/'+account.ParticipantId+'/Permissions',{Revision:management.Data.Revision,ControlPlayback:false,EditQueue:false})).Status,200);
    assert.equal((await api(owner,path+'/Accounts/'+account.ParticipantId+'/Permissions',{Revision:management.Data.Revision,ControlPlayback:true,EditQueue:true})).Status,409);
    const fresh=await api(owner,path+'/Management');assert.equal(fresh.Data.Accounts.find(row=>row.ParticipantId===account.ParticipantId).EffectivePermissions.ControlPlayback,false);
    passed.push('owner permission change and stale review rejection');
    stage='kick command uses the current opaque participant identity';
    assert.equal((await api(owner,path+'/Participants/'+account.ParticipantId+'/Moderate',{Revision:fresh.Data.Revision,Action:'kick-account'})).Status,200);
    stage='verify kicked native membership';
    assert.equal((await api(peer,'WatchParty')).Data.Active,false);
    stage='new blocked-account invitation';const blockedInvite=await api(owner,path+'/Invites',{});assert.equal(blockedInvite.Status,200);
    stage='blocked account native admission rejection';await admission(peer,blockedInvite.Data,403);assert.equal((await api(peer,'WatchParty')).Data.Active,false);
    stage='unblock does not silently rejoin the account';const blocked=await api(owner,path+'/Management');const target=blocked.Data.BlockedAccounts.find(row=>row.ParticipantId===account.ParticipantId);assert.ok(target);
    assert.equal((await api(owner,path+'/Participants/'+target.ParticipantId+'/Moderate',{Revision:blocked.Data.Revision,Action:'unblock-account'})).Status,200);assert.equal((await api(peer,'WatchParty')).Data.Active,false);
    stage='revocation cannot be bypassed after unblock';assert.equal((await api(owner,path+'/Invites/'+blockedInvite.Data.InviteId+'/Revoke',{})).Status,200);await admission(peer,blockedInvite.Data,404);assert.equal((await api(peer,'WatchParty')).Data.Active,false);
    stage='explicit new admission after unblock';const nextInvite=await api(owner,path+'/Invites',{});assert.equal(nextInvite.Status,200);await admission(peer,nextInvite.Data,200);assert.equal((await api(peer,'WatchParty')).Data.Active,true);
    stage='owner-only end removes both exact native memberships';assert.equal((await api(owner,path+'/End',{})).Status,200);assert.equal((await api(owner,'WatchParty')).Data.Active,false);assert.equal((await api(peer,'WatchParty')).Data.Active,false);
    console.log(JSON.stringify({Fixture:state.Name,Provider:'0.1.5',Sdk:'1.1.0',TwoRealNativeClients:true,NativeProviderAdmission:true,BothClientsDecodedGeneratedVideo:true,AdministratorOwnerBypassRejected:true,NativePlayerPartyBypassRejected:true,OwnerPermissionsAndStaleReview:true,KickBlockExplicitUnblockRejoin:true,EndClearsMemberships:true,PublicHttpsProxyFlowTested:false,GuestAccessEnabledByTest:false,CredentialsOrInvitationLogged:false}));
}catch(error){const primitive=value=>typeof value==='number'||typeof value==='boolean'?value:undefined;console.error(JSON.stringify({Stage:stage,LastStatus:lastStatus,Actual:primitive(error.actual),Expected:primitive(error.expected),Passed:passed,NoUncertainWriteRetried:true,PrivateLogsSuppressed:true}));process.exitCode=1;}
finally{await browser.close();}
